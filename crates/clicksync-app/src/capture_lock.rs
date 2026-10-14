use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows::Win32::{
    Foundation::{HWND, POINT, RECT},
    Graphics::Gdi::ClientToScreen,
    UI::WindowsAndMessaging::{
        ClipCursor, GetClientRect, GetClipCursor, GetCursorPos, GetForegroundWindow,
        GetWindowThreadProcessId, IsWindow, SetCursorPos, ShowCursor,
    },
};

#[cfg(feature = "ui-restoration-probe")]
use windows::Win32::UI::WindowsAndMessaging::{CURSOR_SHOWING, CURSORINFO, GetCursorInfo};

const VISIBILITY_LIMIT: u32 = 24;
const CLIP_HALF_SIZE: i32 = 12;

trait CursorApi {
    fn lock_rect(&self) -> Result<RECT, String>;
    fn position(&self) -> Result<POINT, String>;
    fn clip(&self) -> Result<RECT, String>;
    fn set_position(&self, point: POINT) -> Result<(), String>;
    fn set_clip(&self, rect: RECT) -> Result<(), String>;
    fn show(&self, visible: bool) -> i32;
}

#[derive(Default)]
struct NativeCursor {
    hwnd: HWND,
}

impl NativeCursor {
    fn validate(&self) -> Result<(), String> {
        let mut pid = 0;
        unsafe { GetWindowThreadProcessId(self.hwnd, Some(&mut pid)) };
        if self.hwnd.0.is_null()
            || !unsafe { IsWindow(Some(self.hwnd)) }.as_bool()
            || pid != std::process::id()
            || unsafe { GetForegroundWindow() } != self.hwnd
        {
            return Err("the capture window is no longer in the foreground".into());
        }
        Ok(())
    }
}

impl CursorApi for NativeCursor {
    fn lock_rect(&self) -> Result<RECT, String> {
        self.validate()?;
        let mut rect = RECT::default();
        unsafe { GetClientRect(self.hwnd, &mut rect) }.map_err(|e| e.to_string())?;
        if rect.right <= rect.left || rect.bottom <= rect.top {
            return Err("capture window has no visible client area".into());
        }
        let mut center = POINT {
            x: (rect.right - rect.left) / 2,
            y: (rect.bottom - rect.top) / 2,
        };
        if !unsafe { ClientToScreen(self.hwnd, &mut center) }.as_bool() {
            return Err("window coordinates unavailable".into());
        }
        self.validate()?;
        Ok(RECT {
            left: center.x - CLIP_HALF_SIZE,
            top: center.y - CLIP_HALF_SIZE,
            right: center.x + CLIP_HALF_SIZE,
            bottom: center.y + CLIP_HALF_SIZE,
        })
    }
    fn position(&self) -> Result<POINT, String> {
        let mut point = POINT::default();
        unsafe { GetCursorPos(&mut point) }.map_err(|e| e.to_string())?;
        Ok(point)
    }
    fn clip(&self) -> Result<RECT, String> {
        let mut rect = RECT::default();
        unsafe { GetClipCursor(&mut rect) }.map_err(|e| e.to_string())?;
        Ok(rect)
    }
    fn set_position(&self, point: POINT) -> Result<(), String> {
        unsafe { SetCursorPos(point.x, point.y) }.map_err(|e| e.to_string())
    }
    fn set_clip(&self, rect: RECT) -> Result<(), String> {
        unsafe { ClipCursor(Some(&rect)) }.map_err(|e| e.to_string())
    }
    fn show(&self, visible: bool) -> i32 {
        unsafe { ShowCursor(visible) }
    }
}

struct SavedCursor {
    position: POINT,
    clip: RECT,
    applied_clip: RECT,
    hide_calls: u32,
}

#[derive(Default)]
struct LockState {
    saved: Option<SavedCursor>,
}

impl LockState {
    fn acquire(&mut self, api: &impl CursorApi) -> Result<(), String> {
        let rect = api.lock_rect()?;
        if self.saved.is_some() {
            return Ok(());
        }
        let position = api.position()?;
        let clip = api.clip()?;
        self.saved = Some(SavedCursor {
            position,
            clip,
            applied_clip: clip,
            hide_calls: 0,
        });
        let result = (|| {
            api.set_clip(rect)?;
            self.saved.as_mut().unwrap().applied_clip = rect;
            api.set_position(POINT {
                x: (rect.left + rect.right) / 2,
                y: (rect.top + rect.bottom) / 2,
            })?;
            for _ in 0..VISIBILITY_LIMIT {
                let count = api.show(false);
                self.saved.as_mut().unwrap().hide_calls += 1;
                if count < 0 {
                    // A focus change during acquisition rolls back before capture opens.
                    api.lock_rect()?;
                    return Ok(());
                }
            }
            Err("cursor visibility counter did not become hidden".into())
        })();
        if let Err(error) = result {
            let cleanup = self.release(api);
            return Err(match cleanup {
                Ok(()) => error,
                Err(cleanup) => format!("{error}; cursor restoration failed: {cleanup}"),
            });
        }
        Ok(())
    }
    fn confine(&mut self, api: &impl CursorApi) -> Result<(), String> {
        let rect = api.lock_rect()?;
        let saved = self.saved.as_mut().ok_or("capture cursor is not locked")?;
        if saved.applied_clip != rect || api.clip()? != rect {
            api.set_clip(rect)?;
            saved.applied_clip = rect;
        }
        Ok(())
    }
    fn release(&mut self, api: &impl CursorApi) -> Result<(), String> {
        let Some(saved) = self.saved.take() else {
            return Ok(());
        };
        // Balance exactly our calls, including an already hidden cursor.
        for _ in 0..saved.hide_calls {
            api.show(true);
        }
        // Another foreground application may already own a different clip rectangle.
        if api.clip()? == saved.applied_clip {
            api.set_clip(saved.clip)?;
            api.set_position(saved.position)?;
        }
        Ok(())
    }
}

#[derive(Default)]
pub struct CaptureLock {
    api: NativeCursor,
    state: LockState,
}
impl CaptureLock {
    pub fn for_window(window: &gpui::Window) -> Result<Self, String> {
        let handle = HasWindowHandle::window_handle(window).map_err(|e| e.to_string())?;
        let RawWindowHandle::Win32(handle) = handle.as_raw() else {
            return Err("capture requires a Win32 window".into());
        };
        Ok(Self {
            api: NativeCursor {
                hwnd: HWND(handle.hwnd.get() as *mut _),
            },
            state: LockState::default(),
        })
    }
    pub fn window_id(&self) -> usize {
        self.api.hwnd.0 as usize
    }
    pub fn locked(&self) -> bool {
        self.state.saved.is_some()
    }
    pub fn acquire(&mut self) -> Result<(), String> {
        self.state.acquire(&self.api)
    }
    pub fn confine(&mut self) -> Result<(), String> {
        self.state.confine(&self.api)
    }
    pub fn release(&mut self) {
        if let Err(error) = self.state.release(&self.api) {
            eprintln!("capture cursor restoration failed: {error}");
        }
    }
}
impl Drop for CaptureLock {
    fn drop(&mut self) {
        self.release();
    }
}

#[cfg(feature = "ui-restoration-probe")]
pub(crate) struct CursorObservation {
    pub clip: RECT,
    pub showing: bool,
}
#[cfg(feature = "ui-restoration-probe")]
pub(crate) fn cursor_observation() -> gpui::Result<CursorObservation> {
    let mut clip = RECT::default();
    let mut info = CURSORINFO {
        cbSize: std::mem::size_of::<CURSORINFO>() as u32,
        ..Default::default()
    };
    unsafe {
        GetClipCursor(&mut clip)?;
        GetCursorInfo(&mut info)?;
    }
    Ok(CursorObservation {
        clip,
        showing: info.flags == CURSOR_SHOWING,
    })
}
pub fn now_ms() -> f64 {
    let (mut ticks, mut frequency) = (0, 0);
    unsafe {
        if windows::Win32::System::Performance::QueryPerformanceFrequency(&mut frequency).is_err()
            || windows::Win32::System::Performance::QueryPerformanceCounter(&mut ticks).is_err()
        {
            return 0.0;
        }
    }
    ticks as f64 * 1000.0 / frequency as f64
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    struct FakeCursor {
        active: Cell<bool>,
        position: Cell<POINT>,
        clip: Cell<RECT>,
        count: Cell<i32>,
        position_fails: Cell<bool>,
        lose_focus_on_hide: bool,
    }
    impl FakeCursor {
        fn new(count: i32) -> Self {
            Self {
                active: Cell::new(true),
                position: Cell::new(POINT { x: 50, y: 60 }),
                clip: Cell::new(RECT {
                    left: 0,
                    top: 0,
                    right: 1000,
                    bottom: 800,
                }),
                count: Cell::new(count),
                position_fails: Cell::new(false),
                lose_focus_on_hide: false,
            }
        }
    }
    impl CursorApi for FakeCursor {
        fn lock_rect(&self) -> Result<RECT, String> {
            self.active
                .get()
                .then_some(RECT {
                    left: 100,
                    top: 100,
                    right: 124,
                    bottom: 124,
                })
                .ok_or_else(|| "wrong foreground window".into())
        }
        fn position(&self) -> Result<POINT, String> {
            Ok(self.position.get())
        }
        fn clip(&self) -> Result<RECT, String> {
            Ok(self.clip.get())
        }
        fn set_position(&self, point: POINT) -> Result<(), String> {
            if self.position_fails.replace(false) {
                return Err("position failed".into());
            }
            self.position.set(point);
            Ok(())
        }
        fn set_clip(&self, rect: RECT) -> Result<(), String> {
            self.clip.set(rect);
            Ok(())
        }
        fn show(&self, visible: bool) -> i32 {
            self.count
                .set(self.count.get() + if visible { 1 } else { -1 });
            if !visible && self.lose_focus_on_hide {
                self.active.set(false);
            }
            self.count.get()
        }
    }
    #[test]
    fn repeated_lock_and_release_restore_exact_cursor_state() {
        for count in [-3, -1, 0, 4] {
            let api = FakeCursor::new(count);
            let original = (api.position.get(), api.clip.get());
            let mut lock = LockState::default();
            for _ in 0..100 {
                lock.acquire(&api).unwrap();
                let hidden = api.count.get();
                assert!(hidden < 0);
                lock.acquire(&api).unwrap();
                lock.confine(&api).unwrap();
                assert_eq!(api.count.get(), hidden);
                lock.release(&api).unwrap();
                lock.release(&api).unwrap();
                assert_eq!(api.count.get(), count);
                assert_eq!((api.position.get(), api.clip.get()), original);
            }
        }
    }
    #[test]
    fn acquisition_failure_and_focus_race_roll_back_all_changes() {
        for case in 0..4 {
            let mut api = FakeCursor::new(if case == 3 { 40 } else { 0 });
            if case == 0 {
                api.active.set(false);
            }
            if case == 1 {
                api.position_fails.set(true);
            }
            if case == 2 {
                api.lose_focus_on_hide = true;
            }
            let original = (api.position.get(), api.clip.get(), api.count.get());
            let mut lock = LockState::default();
            assert!(lock.acquire(&api).is_err());
            assert!(lock.saved.is_none());
            assert_eq!(
                (api.position.get(), api.clip.get(), api.count.get()),
                original
            );
        }
    }
    #[test]
    fn focus_loss_never_confines_another_window_or_overwrites_its_clip() {
        let api = FakeCursor::new(0);
        let mut lock = LockState::default();
        lock.acquire(&api).unwrap();
        api.active.set(false);
        assert!(lock.confine(&api).is_err());
        let foreign = RECT {
            left: 200,
            top: 200,
            right: 400,
            bottom: 400,
        };
        api.clip.set(foreign);
        lock.release(&api).unwrap();
        assert_eq!(api.clip.get(), foreign);
        assert_eq!(api.count.get(), 0);
    }
}
