//! App-owned services and the sole bounded result receiver.
use super::*;
use crate::backend::{AppStatus, Backend};
use clicksync_core::protocol::{CaptureAck, ControlCommand, ControlInputBatch};
use std::{
    cell::Cell,
    future::Future,
    task::{Poll, Waker},
};

struct FrameWake {
    window: gpui::AnyWindowHandle,
    ready: Cell<bool>,
    waker: RefCell<Option<Waker>>,
}

impl FrameWake {
    fn next(window: &Window) -> Rc<Self> {
        let wake = Rc::new(Self {
            window: window.window_handle(),
            ready: Cell::new(false),
            waker: RefCell::default(),
        });
        let frame = wake.clone();
        window.on_next_frame(move |_, _| {
            frame.ready.set(true);
            if let Some(waker) = frame.waker.borrow_mut().take() {
                waker.wake();
            }
        });
        wake
    }

    async fn wait(&self) {
        // A hidden/minimized window must not stall control ACKs or stop/drain.
        let mut fallback = std::pin::pin!(Timer::after(Duration::from_millis(20)));
        std::future::poll_fn(|cx| {
            if self.ready.get() || fallback.as_mut().poll(cx).is_ready() {
                Poll::Ready(())
            } else {
                *self.waker.borrow_mut() = Some(cx.waker().clone());
                Poll::Pending
            }
        })
        .await;
        self.waker.borrow_mut().take();
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct ReceiverId(pub u64);

pub(super) struct PendingControl {
    pub action: &'static str,
    receiver: ReceiverId,
    reply: std::sync::mpsc::Receiver<Result<CaptureAck, String>>,
}
pub(super) struct PendingMotion {
    pub id: u64,
    receiver: ReceiverId,
    capture: bool,
    armed: bool,
    reply: std::sync::mpsc::Receiver<Result<CaptureAck, String>>,
}

pub(super) struct RuntimeEvents {
    pub motion: Option<Result<clicksync_capture::observation::MotionResult, String>>,
    pub motion_armed: Option<(bool, CaptureAck)>,
    pub control: Option<(&'static str, Result<CaptureAck, String>)>,
    pub inputs: Option<ControlInputBatch>,
    pub chart: Option<Result<Arc<crate::chart::ChartResult>, String>>,
    pub monitors: Vec<(usize, Result<Arc<crate::chart::ChartResult>, String>)>,
    pub domain: Option<Arc<crate::product::AnalysisFrame>>,
    pub summary: Option<Arc<crate::product::performance_summary::SummaryFrame>>,
    pub file: Option<Result<FileResult, String>>,
    pub settings_error: Option<String>,
}

pub(super) struct AppRuntime {
    pub backend: Option<Backend>,
    pub worker: crate::chart::ChartWorker,
    pub domains: DomainWorker,
    pub polling_stream: crate::product::polling_stream::Worker,
    pub files: FileWorker,
    pub settings: Settings,
    pub preferred_device: Option<u32>,
    pub device_catalog: clicksync_core::devices::DeviceCatalog,
    pub pending_control: Option<PendingControl>,
    pub pending_motion: Option<PendingMotion>,
    page_reset_pending: bool,
    current_receiver: Option<ReceiverId>,
    next_receiver: u64,
    file_receiver: Option<ReceiverId>,
    file_inflight: bool,
    settings_dirty: bool,
    closed: bool,
    pub polls: u64,
    pub discarded_results: u64,
    pub driver_starts: u64,
    driver_target: Option<(gpui::AnyWindowHandle, gpui::WeakEntity<GpuiHost>)>,
}

/// Send-only startup work; foreground ownership is attached after initialization.
pub(super) struct StartupServices {
    backend: Option<Backend>,
    worker: crate::chart::ChartWorker,
    domains: DomainWorker,
    polling_stream: crate::product::polling_stream::Worker,
    files: FileWorker,
    settings: Settings,
    error: Option<String>,
}

impl StartupServices {
    pub fn new(interactive: bool) -> Self {
        let (backend, error) = if interactive {
            match Backend::spawn_in_process(false) {
                Ok(backend) => (Some(backend), None),
                Err(error) => (None, Some(error)),
            }
        } else {
            (None, None)
        };
        let settings = if interactive {
            Settings::load()
        } else {
            Settings::default()
        };
        let byte_budget = backend
            .as_ref()
            .map(|backend| backend.byte_budget())
            .unwrap_or_else(|| {
                clicksync_core::SharedByteBudget::new(
                    clicksync_core::SessionConfig::default().managed_memory_budget_bytes,
                )
            });
        Self {
            backend,
            worker: crate::chart::ChartWorker::spawn_with_budget(byte_budget),
            domains: DomainWorker::spawn(),
            polling_stream: crate::product::polling_stream::Worker::spawn(),
            files: FileWorker::spawn(),
            settings,
            error,
        }
    }
}

impl AppRuntime {
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub fn new(interactive: bool) -> (Rc<RefCell<Self>>, Option<String>) {
        Self::from_services(StartupServices::new(interactive))
    }

    pub fn from_services(services: StartupServices) -> (Rc<RefCell<Self>>, Option<String>) {
        (
            Rc::new(RefCell::new(Self {
                backend: services.backend,
                worker: services.worker,
                domains: services.domains,
                polling_stream: services.polling_stream,
                files: services.files,
                settings: services.settings,
                preferred_device: None,
                device_catalog: Default::default(),
                pending_control: None,
                pending_motion: None,
                page_reset_pending: false,
                current_receiver: None,
                next_receiver: 1,
                file_receiver: None,
                file_inflight: false,
                settings_dirty: false,
                closed: false,
                polls: 0,
                discarded_results: 0,
                driver_starts: 0,
                driver_target: None,
            })),
            services.error,
        )
    }
    pub fn attach(&mut self) -> ReceiverId {
        let receiver = ReceiverId(self.next_receiver);
        self.next_receiver += 1;
        self.current_receiver = Some(receiver);
        receiver
    }
    pub fn attach_after_handoff(&mut self, previous: ReceiverId) -> ReceiverId {
        let receiver = self.attach();
        if let Some(pending) = &mut self.pending_control
            && pending.receiver == previous
        {
            pending.receiver = receiver;
        }
        receiver
    }
    pub fn invalidate(&mut self, receiver: ReceiverId) {
        if self.current_receiver == Some(receiver) {
            self.current_receiver = None;
            self.polling_stream.clear(receiver.0);
            for slot in 0..crate::chart::trial::MAX_DEMANDS {
                self.worker.remove_view(slot);
            }
            if let Some(backend) = &self.backend {
                backend.set_capture_permitted(false);
            }
            self.cancel_motion();
        }
    }
    pub fn status(&self) -> Option<AppStatus> {
        self.backend.as_ref().map(Backend::state)
    }
    pub fn accepts_receiver(&self, receiver: ReceiverId) -> bool {
        !self.closed && self.current_receiver == Some(receiver)
    }
    pub fn pending_action(&self) -> Option<&'static str> {
        self.pending_control
            .as_ref()
            .map(|pending| pending.action)
            .or(self.page_reset_pending.then_some("reset"))
            .or(self
                .pending_motion
                .as_ref()
                .filter(|motion| !motion.armed)
                .map(|_| "observe"))
    }
    pub fn observe_motion(
        &mut self,
        receiver: ReceiverId,
        excluded: Option<u32>,
        capture: bool,
        timeout: Option<Duration>,
    ) -> Result<u64, String> {
        if !self.accepts_receiver(receiver) {
            return Err("motion receiver expired".into());
        }
        if self.pending_action().is_some() || self.pending_motion.is_some() {
            return Err("motion control busy".into());
        }
        let backend = self.backend.as_ref().ok_or("capture backend unavailable")?;
        let (id, reply) = backend.observe_motion(excluded, capture, timeout)?;
        self.pending_motion = Some(PendingMotion {
            id,
            receiver,
            capture,
            armed: false,
            reply,
        });
        Ok(id)
    }
    pub fn cancel_motion(&mut self) {
        if let Some(motion) = self.pending_motion.take()
            && let Some(backend) = &self.backend
        {
            if motion.capture {
                backend.set_capture_permitted(false);
                let _ = backend.enqueue(ControlCommand::Pause);
            }
            backend.cancel_motion(motion.id);
        }
    }
    pub fn request_page_reset(&mut self) -> u64 {
        if let Some(backend) = &self.backend {
            backend.set_capture_permitted(false);
            self.page_reset_pending = true;
            backend.state().generation + 1
        } else {
            0
        }
    }
    pub fn enqueue_control(
        &mut self,
        receiver: ReceiverId,
        action: &'static str,
        command: ControlCommand,
    ) -> Result<(), String> {
        if self.closed || self.current_receiver != Some(receiver) {
            return Err("capture receiver expired".into());
        }
        if self.pending_control.is_some() || self.page_reset_pending {
            return Err("capture control busy".into());
        }
        let backend = self.backend.as_ref().ok_or("capture backend unavailable")?;
        let reply = backend.enqueue_tracked(command)?;
        self.pending_control = Some(PendingControl {
            action,
            receiver,
            reply,
        });
        Ok(())
    }
    #[cfg(test)]
    pub fn submit_chart(
        &mut self,
        receiver: ReceiverId,
        request: crate::chart::ChartRequest,
    ) -> Result<(), String> {
        if self.closed || self.current_receiver != Some(receiver) {
            return Err("chart receiver expired".into());
        }
        self.worker.submit_for(receiver.0, request)
    }
    pub fn submit_monitor(
        &mut self,
        receiver: ReceiverId,
        slot: usize,
        request: crate::chart::ChartRequest,
    ) -> Result<(), String> {
        if self.closed || self.current_receiver != Some(receiver) {
            return Err("chart receiver expired".into());
        }
        self.worker.submit_view(receiver.0, slot, request)
    }
    pub fn submit_domain(&mut self, receiver: ReceiverId, request: AnalysisRequest) {
        if !self.closed && self.current_receiver == Some(receiver) {
            self.domains.submit_for(receiver.0, request);
        }
    }
    pub fn submit_summary(
        &self,
        receiver: ReceiverId,
        request: crate::product::performance_summary::SummaryRequest,
    ) {
        if !self.closed && self.current_receiver == Some(receiver) {
            self.domains.submit_summary_for(receiver.0, request);
        }
    }
    pub fn submit_file(
        &mut self,
        receiver: ReceiverId,
        request: FileRequest,
    ) -> Result<(), String> {
        if self.closed || self.current_receiver != Some(receiver) {
            return Err("file receiver expired".into());
        }
        if self.file_inflight {
            return Err("file worker busy".into());
        }
        self.files.submit(request)?;
        self.file_receiver = Some(receiver);
        self.file_inflight = true;
        Ok(())
    }
    pub fn update_settings(&mut self, settings: Settings, persist: bool) {
        self.settings = settings;
        self.settings_dirty |= persist;
    }
    /// The driver calls this once per turn; view callbacks never call worker.take().
    pub fn poll(&mut self, receiver: ReceiverId) -> RuntimeEvents {
        self.polls += 1;
        let mut events = RuntimeEvents {
            motion: None,
            motion_armed: None,
            control: None,
            inputs: None,
            chart: None,
            monitors: Vec::new(),
            domain: None,
            summary: None,
            file: None,
            settings_error: None,
        };
        let live = !self.closed && self.current_receiver == Some(receiver);
        if self
            .pending_motion
            .as_ref()
            .is_some_and(|motion| !live || motion.receiver != receiver)
        {
            self.cancel_motion();
            self.discarded_results += 1;
        }
        if let Some(motion) = &mut self.pending_motion {
            if !motion.armed {
                match motion.reply.try_recv() {
                    Ok(Ok(ack)) => {
                        motion.armed = true;
                        events.motion_armed = Some((motion.capture, ack));
                    }
                    Ok(Err(error)) => events.motion = Some(Err(error)),
                    Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                        events.motion = Some(Err("motion arm reply disconnected".into()))
                    }
                    Err(std::sync::mpsc::TryRecvError::Empty) => {}
                }
            }
            if motion.armed
                && let Some(backend) = &self.backend
                && let Some(result) = backend.take_motion(motion.id)
            {
                events.motion = Some(Ok(result));
            }
        }
        if events.motion.is_some() {
            if events.motion.as_ref().is_some_and(Result::is_err) {
                self.cancel_motion();
            } else {
                self.pending_motion = None;
            }
        }
        if let Some(pending) = &self.pending_control {
            let answer = match pending.reply.try_recv() {
                Ok(answer) => Some(answer),
                Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                    Some(Err("capture control reply disconnected".into()))
                }
                Err(std::sync::mpsc::TryRecvError::Empty) => None,
            };
            if let Some(answer) = answer {
                let pending = self.pending_control.take().unwrap();
                if live && pending.receiver == receiver {
                    events.control = Some((pending.action, answer));
                } else {
                    self.discarded_results += 1;
                }
            }
        }
        if self.page_reset_pending
            && self.pending_control.is_none()
            && !self.closed
            && let Some(current) = self.current_receiver
        {
            self.page_reset_pending = false;
            let generation = self
                .backend
                .as_ref()
                .map_or(1, |backend| backend.state().generation + 1);
            if let Err(error) =
                self.enqueue_control(current, "reset", ControlCommand::Reset { generation })
                && live
            {
                events.control = Some(("reset", Err(error)));
            }
        }
        if let Some(backend) = &self.backend {
            let inputs = backend.take_inputs();
            if live {
                events.inputs = Some(inputs);
            }
        }
        if let Some((owner, answer)) = self.worker.take_completion() {
            if live && owner == receiver.0 {
                events.chart = Some(answer);
            } else {
                self.discarded_results += 1;
            }
        }
        for slot in 1..crate::chart::trial::MAX_DEMANDS {
            if let Some((owner, answer)) = self.worker.take_view_completion(slot) {
                if live && owner == receiver.0 {
                    events.monitors.push((slot, answer));
                } else {
                    self.discarded_results += 1;
                }
            }
        }
        if let Some((owner, answer)) = self.domains.take_completion() {
            if live && owner == receiver.0 {
                events.domain = Some(answer);
            } else {
                self.discarded_results += 1;
            }
        }
        if let Some((owner, answer)) = self.domains.take_summary_completion() {
            if live && owner == receiver.0 {
                events.summary = Some(answer);
            } else {
                self.discarded_results += 1;
            }
        }
        if let Some(answer) = self.files.take() {
            // Clear only this completed operation before checking its receiver.
            let owner = self.file_receiver.take();
            self.file_inflight = false;
            if live && owner == Some(receiver) {
                events.file = Some(answer);
            } else if owner.is_some() {
                self.discarded_results += 1;
            } else if let Err(error) = answer {
                events.settings_error = Some(error);
            }
        }
        if self.settings_dirty
            && !self.file_inflight
            && !self.closed
            && self
                .files
                .submit(FileRequest::Settings(self.settings.clone()))
                .is_ok()
        {
            self.settings_dirty = false;
            self.file_receiver = None;
            self.file_inflight = true;
        }
        events
    }
    pub fn shutdown(&mut self) -> Result<(), String> {
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        self.cancel_motion();
        self.current_receiver = None;
        self.page_reset_pending = false;
        self.driver_target = None;
        if let Some(backend) = &self.backend {
            backend.set_capture_permitted(false);
        }
        let capture = self
            .backend
            .take()
            .map_or(Ok(()), |mut backend| backend.shutdown());
        self.worker.shutdown();
        self.domains.shutdown();
        self.polling_stream.shutdown();
        let settings = self.settings_dirty.then(|| self.settings.clone());
        self.settings_dirty = false;
        let files = self.files.finish(settings);
        capture.and(files)
    }
    pub fn is_closed(&self) -> bool {
        self.closed
    }

    pub fn bind_driver(
        runtime: &Rc<RefCell<Self>>,
        window: &Window,
        host: &gpui::Entity<GpuiHost>,
        cx: &mut gpui::App,
    ) {
        runtime.borrow_mut().driver_target = Some((window.window_handle(), host.downgrade()));
        Self::start_driver(runtime, cx);
    }
    pub fn start_driver(runtime: &Rc<RefCell<Self>>, cx: &mut gpui::App) {
        if runtime.borrow().driver_starts != 0 {
            return;
        }
        runtime.borrow_mut().driver_starts += 1;
        let runtime = runtime.clone();
        cx.spawn(async move |cx| {
            let mut frame: Option<Rc<FrameWake>> = None;
            loop {
                if let Some(frame) = &frame {
                    frame.wait().await;
                } else {
                    Timer::after(Duration::from_millis(20)).await;
                }
                if runtime.borrow().is_closed() {
                    break;
                }
                let target = runtime.borrow().driver_target.clone();
                if let Some((handle, host)) = target {
                    let updated = handle.update(cx, |_, window, cx| {
                        host.update(cx, |host, cx| {
                            let changed = host.tick_chart(window, cx);
                            host.sync_polling_history(cx);
                            if changed {
                                host.invalidate_polling_content(cx);
                                cx.notify();
                            }
                            host.page == Page::Performance
                                && host.workspace.chart_window
                                && !host.workspace.suspended
                                && host.lock.locked()
                        })
                        .map(|live| {
                            if live {
                                // Keep at most one callback on this window across fallback ticks.
                                if frame
                                    .as_ref()
                                    .is_none_or(|f| f.ready.get() || f.window != handle)
                                {
                                    frame = Some(FrameWake::next(window));
                                }
                            } else {
                                frame = None;
                            }
                        })
                    });
                    if !matches!(updated, Ok(Ok(()))) {
                        runtime.borrow_mut().driver_target = None;
                        frame = None;
                    }
                } else {
                    frame = None;
                    runtime.borrow_mut().poll(ReceiverId(0));
                }
            }
        })
        .detach();
    }
}
impl Drop for AppRuntime {
    fn drop(&mut self) {
        if let Err(error) = self.shutdown() {
            eprintln!("application runtime shutdown failed: {error}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn import(path: PathBuf) -> FileRequest {
        FileRequest::ImportAnalysis {
            path,
            operation: 1,
            budget: clicksync_core::SharedByteBudget::new(
                clicksync_core::SessionConfig::default().managed_memory_budget_bytes,
            ),
            existing: Vec::new(),
        }
    }

    #[test]
    fn late_file_completion_is_discarded_and_a_new_receiver_can_submit() {
        let (runtime, error) = AppRuntime::new(false);
        assert!(error.is_none());
        let first = runtime.borrow_mut().attach();
        runtime
            .borrow_mut()
            .submit_file(first, import(PathBuf::from("missing-u3.json")))
            .unwrap();
        runtime.borrow_mut().invalidate(first);
        let second = runtime.borrow_mut().attach();
        let deadline = std::time::Instant::now() + Duration::from_secs(3);
        while runtime.borrow().file_receiver.is_some() {
            assert!(std::time::Instant::now() < deadline);
            let events = runtime.borrow_mut().poll(second);
            assert!(events.file.is_none());
            std::thread::yield_now();
        }
        assert_eq!(runtime.borrow().discarded_results, 1);
        runtime
            .borrow_mut()
            .submit_file(second, import(PathBuf::from("missing-u3-again.json")))
            .unwrap();
        loop {
            assert!(std::time::Instant::now() < deadline);
            if let Some(answer) = runtime.borrow_mut().poll(second).file {
                assert!(answer.is_err());
                break;
            }
            std::thread::yield_now();
        }
        runtime.borrow_mut().shutdown().unwrap();
        runtime.borrow_mut().shutdown().unwrap();
    }

    #[test]
    fn an_old_chart_error_is_not_reassigned_when_the_next_view_submits() {
        use clicksync_core::{Session, SessionConfig, SessionMetadata, SharedByteBudget};
        let (runtime, _) = AppRuntime::new(false);
        let config = SessionConfig::default();
        let mut session = Session::new(
            config.clone(),
            SharedByteBudget::new(config.managed_memory_budget_bytes),
            SessionMetadata::default(),
        )
        .unwrap();
        let prefix = Arc::new(crate::backend::SessionPrefix {
            generation: 0,
            received_sequence: 0,
            received_records: 0,
            phase: crate::backend::CapturePhase::Empty,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        });
        let mut request = crate::chart::ChartRequest {
            id: 1,
            source_version: 0,
            prefix,
            parameters: clicksync_chart::ChartParameters::new(clicksync_chart::Metric::Count(
                clicksync_chart::Axis::X,
            )),
            dataset_cpi: Vec::new(),
            secondary_metric: None,
            kind: clicksync_chart::gpu::PlotKind::Lines,
            view: crate::chart::ViewMode::FullSession,
            analysis: None,
        };
        request.parameters.options.minimum_trend_samples = 0;
        let first = runtime.borrow_mut().attach();
        runtime
            .borrow_mut()
            .submit_chart(first, request.clone())
            .unwrap();
        let deadline = std::time::Instant::now() + Duration::from_secs(3);
        while runtime.borrow().worker.report().processed_targets == 0 {
            assert!(std::time::Instant::now() < deadline);
            std::thread::yield_now();
        }
        runtime.borrow_mut().invalidate(first);
        let second = runtime.borrow_mut().attach();
        request.id = 2;
        request.parameters.options = Default::default();
        runtime.borrow_mut().submit_chart(second, request).unwrap();
        loop {
            assert!(std::time::Instant::now() < deadline);
            if let Some(answer) = runtime.borrow_mut().poll(second).chart {
                assert!(answer.is_ok(), "the new view received the old error");
                assert_eq!(answer.unwrap().request.id, 2);
                break;
            }
            std::thread::yield_now();
        }
        assert!(
            runtime
                .borrow_mut()
                .submit_file(first, import(PathBuf::new()))
                .is_err()
        );
        runtime.borrow_mut().shutdown().unwrap();
        assert!(
            runtime
                .borrow_mut()
                .submit_file(second, import(PathBuf::new()))
                .is_err()
        );
    }

    #[test]
    fn window_handoff_delivers_the_pending_capture_reply_to_its_successor() {
        let (runtime, _) = AppRuntime::new(false);
        let first = runtime.borrow_mut().attach();
        let (sender, reply) = std::sync::mpsc::channel();
        runtime.borrow_mut().pending_control = Some(PendingControl {
            action: "resume",
            receiver: first,
            reply,
        });
        runtime.borrow_mut().invalidate(first);
        let second = runtime.borrow_mut().attach_after_handoff(first);
        sender
            .send(Ok(CaptureAck {
                command: clicksync_core::protocol::CommandTag::Resume,
                generation: 7,
                cutoff: 23,
                queued: 23,
                ticks: 100,
                timestamp_frequency: 1000,
                complete: true,
                overflow: 0,
                queue_high_watermark: 0,
                devices: Vec::new(),
                error: None,
            }))
            .unwrap();
        let events = runtime.borrow_mut().poll(second);
        let (action, answer) = events.control.unwrap();
        assert_eq!(action, "resume");
        let ack = answer.unwrap();
        assert_eq!((ack.generation, ack.queued), (7, 23));
        assert_eq!(runtime.borrow().discarded_results, 0);
        assert!(runtime.borrow().pending_control.is_none());
        runtime.borrow_mut().shutdown().unwrap();
    }

    #[test]
    fn a_late_control_reply_is_discarded_without_clearing_the_new_view() {
        let (runtime, _) = AppRuntime::new(false);
        let first = runtime.borrow_mut().attach();
        let (sender, reply) = std::sync::mpsc::channel();
        runtime.borrow_mut().pending_control = Some(PendingControl {
            action: "start",
            receiver: first,
            reply,
        });
        runtime.borrow_mut().invalidate(first);
        let second = runtime.borrow_mut().attach();
        sender.send(Err("old start failed".into())).unwrap();
        let events = runtime.borrow_mut().poll(second);
        assert!(events.control.is_none());
        assert!(runtime.borrow().pending_control.is_none());
        assert_eq!(runtime.borrow().current_receiver, Some(second));
        assert_eq!(runtime.borrow().discarded_results, 1);
        runtime.borrow_mut().shutdown().unwrap();
    }
}
