use std::time::Instant;
use windows::Win32::{
    Foundation::{CloseHandle, HANDLE, WAIT_FAILED},
    System::Threading::{
        CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, CancelWaitableTimer, CreateEventW,
        CreateWaitableTimerExW, INFINITE, SetEvent, SetWaitableTimer, TIMER_ALL_ACCESS,
        WaitForMultipleObjects, WaitForSingleObject,
    },
};

/// Handles live until the sampling thread has joined. Win32 events/timers are
/// thread-safe; only the sampling thread arms the timer, producers set the event.
pub(super) struct Waiter {
    event: HANDLE,
    timer: HANDLE,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub high_resolution: bool,
}

unsafe impl Send for Waiter {}
unsafe impl Sync for Waiter {}

impl Waiter {
    pub fn new() -> Self {
        let timer = unsafe {
            CreateWaitableTimerExW(
                None,
                None,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_ALL_ACCESS.0,
            )
        };
        #[cfg(any(test, feature = "ui-restoration-probe"))]
        let high_resolution = timer.is_ok();
        let timer = timer
            .or_else(|_| unsafe { CreateWaitableTimerExW(None, None, 0, TIMER_ALL_ACCESS.0) })
            .expect("polling history timer");
        let event = match unsafe { CreateEventW(None, false, false, None) } {
            Ok(event) => event,
            Err(error) => {
                let _ = unsafe { CloseHandle(timer) };
                panic!("polling history wake event: {error}");
            }
        };
        Self {
            event,
            timer,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            high_resolution,
        }
    }

    pub fn notify(&self) {
        unsafe { SetEvent(self.event) }.expect("polling history wake");
    }

    pub fn wait(&self, deadline: Option<Instant>) {
        let result = if let Some(deadline) = deadline {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return;
            }
            let ticks = remaining.as_nanos().div_ceil(100).min(i64::MAX as u128) as i64;
            unsafe { SetWaitableTimer(self.timer, &-ticks, 0, None, None, false) }
                .expect("polling history timer deadline");
            unsafe { WaitForMultipleObjects(&[self.event, self.timer], false, INFINITE) }
        } else {
            let _ = unsafe { CancelWaitableTimer(self.timer) };
            unsafe { WaitForSingleObject(self.event, INFINITE) }
        };
        assert_ne!(result, WAIT_FAILED, "polling history wait failed");
    }
}

impl Drop for Waiter {
    fn drop(&mut self) {
        unsafe {
            let _ = CancelWaitableTimer(self.timer);
            let _ = CloseHandle(self.timer);
            let _ = CloseHandle(self.event);
        }
    }
}
