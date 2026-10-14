use super::*;
use clicksync_capture::observation::{MotionRequest, MotionResult};

impl Backend {
    pub fn observe_motion(
        &self,
        excluded: Option<u32>,
        capture: bool,
        timeout: Option<Duration>,
    ) -> Result<(u64, mpsc::Receiver<Result<CaptureAck, String>>), String> {
        let shared = self
            .local_shared
            .as_ref()
            .ok_or("motion observation requires the in-process source")?;
        if self.state().command_pending || self.state().phase == CapturePhase::Collecting {
            return Err("motion observation requires an idle capture owner".into());
        }
        let id = shared.motion.reserve().map_err(str::to_owned)?;
        let request = MotionRequest {
            id,
            excluded,
            capture_generation: capture.then(|| self.state().generation + 1),
            timeout,
        };
        let (reply, receiver) = mpsc::sync_channel(1);
        if let Err(error) = self.requests.try_send(DriverRequest::Motion {
            request,
            permit: shared.permit.ticket(),
            reply,
        }) {
            shared.motion.cancel(id);
            return Err(format!("motion control queue unavailable: {error}"));
        }
        Ok((id, receiver))
    }
    pub fn take_motion(&self, id: u64) -> Option<MotionResult> {
        self.local_shared.as_ref()?.motion.take(id)
    }
    pub fn cancel_motion(&self, id: u64) {
        if let Some(shared) = &self.local_shared {
            shared.motion.cancel(id);
            shared.wake.notify();
        }
    }
    #[cfg(feature = "ui-restoration-probe")]
    pub fn replay_motion(
        &self,
        reports: Vec<clicksync_capture::observation::MotionReport>,
    ) -> Result<(), String> {
        if reports.len() > CAPTURE_QUEUE_RECORDS {
            return Err("controlled motion exceeds queue capacity".into());
        }
        let (reply, receiver) = mpsc::sync_channel(1);
        self.requests
            .try_send(DriverRequest::ReplayMotion { reports, reply })
            .map_err(|error| error.to_string())?;
        receiver
            .recv_timeout(IO_TIMEOUT)
            .map_err(|error| error.to_string())?
    }
}

pub(super) fn drive_motion(
    transport: &mut ControlTransport,
    request: MotionRequest,
    permit: u64,
    ingestion: &mpsc::SyncSender<IngestMessage>,
    state: &Arc<Mutex<AppStatus>>,
) -> Result<CaptureAck, String> {
    let ControlTransport::Local(runtime) = transport else {
        return Err("motion observation is not part of the external wire protocol".into());
    };
    state
        .lock()
        .expect("application status lock")
        .command_pending = true;
    let result: Result<CaptureAck, String> = (|| {
        if request.capture_generation.is_some() {
            let (sender, receiver) = mpsc::sync_channel(1);
            ingestion
                .send(IngestMessage::Prepare(sender))
                .map_err(|error| error.to_string())?;
            runtime.shared().wake.notify();
            receiver
                .recv_timeout(IO_TIMEOUT)
                .map_err(|_| "business session owner did not prepare".to_owned())?;
        }
        let ack = runtime
            .observe_motion(request, permit)
            .map_err(|error| error.to_string())?;
        if request.capture_generation.is_some() {
            ingestion
                .send(IngestMessage::MotionArmed {
                    id: request.id,
                    ack: ack.clone(),
                })
                .map_err(|error| error.to_string())?;
            runtime.shared().wake.notify();
        }
        Ok(ack)
    })();
    if let Err(error) = &result {
        runtime.shared().motion.cancel(request.id);
        if request.capture_generation.is_some() {
            let _ = ingestion.send(IngestMessage::Rejected(error.clone()));
        }
        runtime.shared().wake.notify();
    }
    state
        .lock()
        .expect("application status lock")
        .command_pending = false;
    result
}
