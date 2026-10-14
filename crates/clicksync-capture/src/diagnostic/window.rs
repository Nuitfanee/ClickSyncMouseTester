use std::{io, ptr::null_mut};
use windows_sys::Win32::{
    Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM},
    Graphics::Gdi::{COLOR_WINDOW, DEFAULT_GUI_FONT, GetStockObject},
    System::{LibraryLoader::GetModuleHandleW, Threading::GetCurrentProcessId},
    UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetForegroundWindow,
        GetWindowThreadProcessId, IsWindow, MSG, PM_REMOVE, PeekMessageW, PostQuitMessage,
        RegisterClassW, SW_SHOW, SendMessageW, SetForegroundWindow, SetWindowTextW, ShowWindow,
        TranslateMessage, UnregisterClassW, WM_CLOSE, WM_DESTROY, WM_QUIT, WM_SETFONT, WNDCLASSW,
        WS_CHILD, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
    },
};

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_CLOSE => {
            unsafe { DestroyWindow(hwnd) };
            0
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            0
        }
        _ => unsafe { DefWindowProcW(hwnd, message, wparam, lparam) },
    }
}

pub struct ActivationWindow {
    hwnd: HWND,
    label: HWND,
    instance: HINSTANCE,
    class_name: Vec<u16>,
}

impl ActivationWindow {
    pub fn open(title: &str, instructions: &str, x: i32, y: i32) -> io::Result<Self> {
        let instance = unsafe { GetModuleHandleW(null_mut()) };
        if instance.is_null() {
            return Err(io::Error::last_os_error());
        }
        let class_name = wide(&format!("ClickSyncWorkerFocus-{}", std::process::id()));
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            hbrBackground: (COLOR_WINDOW as usize + 1) as _,
            lpszClassName: class_name.as_ptr(),
            ..Default::default()
        };
        if unsafe { RegisterClassW(&class) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let hwnd = unsafe {
            CreateWindowExW(
                0,
                class_name.as_ptr(),
                wide(title).as_ptr(),
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                x,
                y,
                830,
                380,
                null_mut(),
                null_mut(),
                instance,
                null_mut(),
            )
        };
        if hwnd.is_null() {
            unsafe { UnregisterClassW(class_name.as_ptr(), instance) };
            return Err(io::Error::last_os_error());
        }
        let mut window = Self {
            hwnd,
            label: null_mut(),
            instance,
            class_name,
        };
        let label = unsafe {
            CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                wide(instructions).as_ptr(),
                WS_CHILD | WS_VISIBLE,
                25,
                25,
                765,
                275,
                hwnd,
                null_mut(),
                instance,
                null_mut(),
            )
        };
        if label.is_null() {
            return Err(io::Error::last_os_error());
        }
        window.label = label;
        unsafe {
            SendMessageW(
                label,
                WM_SETFONT,
                GetStockObject(DEFAULT_GUI_FONT) as WPARAM,
                1,
            );
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        Ok(window)
    }

    pub fn handle(&self) -> usize {
        self.hwnd as usize
    }

    pub fn foreground_process_id(&self) -> u32 {
        let mut process_id = 0;
        unsafe { GetWindowThreadProcessId(GetForegroundWindow(), &mut process_id) };
        process_id
    }

    pub fn is_foreground(&self) -> bool {
        self.foreground_process_id() == unsafe { GetCurrentProcessId() }
    }

    pub fn pump(&self) -> bool {
        let mut message = MSG::default();
        while unsafe { PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) } != 0 {
            if message.message == WM_QUIT {
                return false;
            }
            unsafe {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        true
    }

    pub fn set_title(&self, title: &str) {
        unsafe { SetWindowTextW(self.hwnd, wide(title).as_ptr()) };
    }

    pub fn set_instructions(&self, instructions: &str) {
        unsafe { SetWindowTextW(self.label, wide(instructions).as_ptr()) };
    }
}

impl Drop for ActivationWindow {
    fn drop(&mut self) {
        unsafe {
            if IsWindow(self.hwnd) != 0 {
                DestroyWindow(self.hwnd);
            }
            UnregisterClassW(self.class_name.as_ptr(), self.instance);
        }
    }
}
