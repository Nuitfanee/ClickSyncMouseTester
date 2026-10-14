use super::*;
use crate::product::polling_stream::Request;

impl GpuiHost {
    pub(in crate::ui) fn configure_polling_sampling(&mut self, prefix: Arc<SessionPrefix>) {
        let now_ms = self.polling.domain_now(&prefix);
        let runtime = self.runtime.borrow();
        if runtime.accepts_receiver(self.receiver)
            && let Some(backend) = &runtime.backend
        {
            runtime.polling_stream.configure(Request {
                receiver: self.receiver.0,
                generation: prefix.generation,
                reset: self.domain_reset,
                mode: PollingMode::MotionReportRate,
                source: backend.prefix_reader(),
                now_ms,
                running: self.lock.locked() && prefix.phase == CapturePhase::Collecting,
                revision: self.polling.revision,
                pause_ms: self.polling.pause_ms,
            });
        }
    }
}
