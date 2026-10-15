//! Windows client-area motion preference. Live charts keep their data clock.
pub(super) fn reduced() -> bool {
    #[cfg(windows)]
    {
        use windows::Win32::UI::WindowsAndMessaging::{
            SPI_GETCLIENTAREAANIMATION, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS, SystemParametersInfoW,
        };
        let mut enabled = windows::core::BOOL(1);
        let read = unsafe {
            SystemParametersInfoW(
                SPI_GETCLIENTAREAANIMATION,
                0,
                Some((&mut enabled as *mut windows::core::BOOL).cast()),
                SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
            )
        };
        read.is_ok() && !enabled.as_bool()
    }
    #[cfg(not(windows))]
    {
        false
    }
}
