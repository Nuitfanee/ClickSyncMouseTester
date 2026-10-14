//! HID descriptor facts are read only on discovery cache misses or failed-query retries.
use super::decode_wide;
use std::{
    mem::size_of_val,
    ptr::{null, null_mut},
};
use windows_sys::Win32::{
    Devices::HumanInterfaceDevice::*,
    Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE},
    Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING},
};

const MAX_INPUT_CAPS: usize = 256;
const GENERIC_DESKTOP: u16 = 0x01;
const MOUSE: u16 = 0x02;
const KEYBOARD: u16 = 0x06;
const KEYBOARD_KEYS: u16 = 0x07;
const DATA_VARIABLE: u16 = 0x02;
const GENERIC_DESKTOP_X: u16 = 0x30;
const GENERIC_DESKTOP_Y: u16 = 0x31;

struct Handle(HANDLE);
impl Handle {
    fn open(path: &str) -> Option<Self> {
        let wide: Vec<_> = path.encode_utf16().chain([0]).collect();
        let handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                null(),
                OPEN_EXISTING,
                0,
                null_mut(),
            )
        };
        (handle != INVALID_HANDLE_VALUE && !handle.is_null()).then_some(Self(handle))
    }
}
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

struct Descriptor {
    data: isize,
    caps: HIDP_CAPS,
}
impl Descriptor {
    fn read(path: &str) -> Option<Self> {
        let handle = Handle::open(path)?;
        let mut data = 0;
        // Raw Input returns no preparsed bytes for mouse/keyboard types on some Windows systems.
        if !unsafe { HidD_GetPreparsedData(handle.0, &mut data) } || data == 0 {
            return None;
        }
        let mut descriptor = Self {
            data,
            caps: HIDP_CAPS::default(),
        };
        if unsafe { HidP_GetCaps(data, &mut descriptor.caps) } != HIDP_STATUS_SUCCESS {
            return None;
        }
        Some(descriptor)
    }

    fn data(&self) -> isize {
        self.data
    }
}
impl Drop for Descriptor {
    fn drop(&mut self) {
        unsafe { HidD_FreePreparsedData(self.data) };
    }
}

pub(super) fn names(path: &str) -> (String, String) {
    let Some(handle) = Handle::open(path) else {
        return Default::default();
    };
    let mut manufacturer = [0u16; 256];
    let mut product = [0u16; 256];
    let manufacturer_ok = unsafe {
        HidD_GetManufacturerString(
            handle.0,
            manufacturer.as_mut_ptr().cast(),
            size_of_val(&manufacturer) as u32,
        )
    };
    let product_ok = unsafe {
        HidD_GetProductString(
            handle.0,
            product.as_mut_ptr().cast(),
            size_of_val(&product) as u32,
        )
    };
    (
        manufacturer_ok
            .then(|| decode_wide(&manufacturer))
            .flatten()
            .unwrap_or_default(),
        product_ok
            .then(|| decode_wide(&product))
            .flatten()
            .unwrap_or_default(),
    )
}

pub(super) fn typing_keyboard(path: &str) -> Option<bool> {
    let data = Descriptor::read(path)?;
    let caps = &data.caps;
    if caps.NumberInputButtonCaps as usize > MAX_INPUT_CAPS {
        return None;
    }
    if (caps.UsagePage, caps.Usage) != (GENERIC_DESKTOP, KEYBOARD)
        || caps.NumberInputButtonCaps == 0
    {
        return Some(false);
    }
    let mut buttons = vec![HIDP_BUTTON_CAPS::default(); caps.NumberInputButtonCaps as usize];
    let mut count = caps.NumberInputButtonCaps;
    if unsafe { HidP_GetButtonCaps(HidP_Input, buttons.as_mut_ptr(), &mut count, data.data()) }
        != HIDP_STATUS_SUCCESS
        || count as usize > buttons.len()
    {
        return None;
    }
    Some(typing_matrix(&buttons[..count as usize]))
}

/// Relative axes prove a declared function, not whether a physical mouse sensor exists.
pub(super) fn relative_mouse(path: &str) -> Option<bool> {
    let data = Descriptor::read(path)?;
    let caps = &data.caps;
    if caps.NumberInputValueCaps as usize > MAX_INPUT_CAPS {
        return None;
    }
    if (caps.UsagePage, caps.Usage) != (GENERIC_DESKTOP, MOUSE) || caps.NumberInputValueCaps == 0 {
        return Some(false);
    }
    let mut values = vec![HIDP_VALUE_CAPS::default(); caps.NumberInputValueCaps as usize];
    let mut count = caps.NumberInputValueCaps;
    if unsafe { HidP_GetValueCaps(HidP_Input, values.as_mut_ptr(), &mut count, data.data()) }
        != HIDP_STATUS_SUCCESS
        || count as usize > values.len()
    {
        return None;
    }
    Some(relative_axes(&values[..count as usize]))
}

fn relative_axes(values: &[HIDP_VALUE_CAPS]) -> bool {
    let mut reports = Vec::<(u8, u16, u8)>::with_capacity(values.len());
    for value in values {
        if value.UsagePage != GENERIC_DESKTOP
            || value.IsAbsolute
            || value.BitField & 0x03 != DATA_VARIABLE
            || value.BitSize == 0
            || value.ReportCount == 0
            || value.LogicalMin >= value.LogicalMax
        {
            continue;
        }
        let (first, last) = unsafe {
            if value.IsRange {
                (
                    value.Anonymous.Range.UsageMin,
                    value.Anonymous.Range.UsageMax,
                )
            } else {
                let usage = value.Anonymous.NotRange.Usage;
                (usage, usage)
            }
        };
        let mut axes = 0u8;
        if first <= GENERIC_DESKTOP_X && GENERIC_DESKTOP_X <= last {
            axes |= 1;
        }
        if first <= GENERIC_DESKTOP_Y && GENERIC_DESKTOP_Y <= last {
            axes |= 2;
        }
        if axes == 0 {
            continue;
        }
        if let Some((_, _, known_axes)) = reports
            .iter_mut()
            .find(|(report, link, _)| *report == value.ReportID && *link == value.LinkCollection)
        {
            *known_axes |= axes;
        } else {
            reports.push((value.ReportID, value.LinkCollection, axes));
        }
    }
    reports.iter().any(|(_, _, axes)| *axes == 3)
}

/// A full keyboard declares independent bits for letters, digits, Enter and Space.
/// Keyboard arrays used for mouse macros and modifier-only bits are not this evidence.
fn typing_matrix(buttons: &[HIDP_BUTTON_CAPS]) -> bool {
    const FIRST: u16 = 0x04; // A
    const ENTER: u16 = 0x28;
    const SPACE: u16 = 0x2c;
    const REQUIRED: u64 = ((1 << (ENTER - FIRST + 1)) - 1) | (1 << (SPACE - FIRST));
    let mut reports = [0u64; 256];
    for button in buttons {
        if button.UsagePage != KEYBOARD_KEYS || button.BitField & 0x03 != DATA_VARIABLE {
            continue;
        }
        let (first, last) = unsafe {
            if button.IsRange {
                (
                    button.Anonymous.Range.UsageMin,
                    button.Anonymous.Range.UsageMax,
                )
            } else {
                let usage = button.Anonymous.NotRange.Usage;
                (usage, usage)
            }
        };
        for usage in first.max(FIRST)..=last.min(SPACE) {
            reports[button.ReportID as usize] |= 1 << (usage - FIRST);
        }
    }
    reports.iter().any(|keys| keys & REQUIRED == REQUIRED)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn axes(first: u16, last: u16, report: u8, link: u16) -> HIDP_VALUE_CAPS {
        HIDP_VALUE_CAPS {
            UsagePage: GENERIC_DESKTOP,
            IsRange: true,
            BitField: DATA_VARIABLE,
            ReportID: report,
            LinkCollection: link,
            BitSize: 16,
            ReportCount: last.saturating_sub(first) + 1,
            LogicalMin: -32767,
            LogicalMax: 32767,
            Anonymous: HIDP_VALUE_CAPS_0 {
                Range: HIDP_VALUE_CAPS_0_0 {
                    UsageMin: first,
                    UsageMax: last,
                    ..Default::default()
                },
            },
            ..Default::default()
        }
    }

    #[test]
    fn relative_xy_requires_the_same_report_and_collection() {
        let x = axes(0x30, 0x30, 255, 7);
        let y = axes(0x31, 0x31, 255, 7);
        assert!(relative_axes(&[x, y]));
        assert!(relative_axes(&[axes(0x30, 0x38, 0, 1)]));
        assert!(!relative_axes(&[x]));
        assert!(!relative_axes(&[y]));
        assert!(!relative_axes(&[x, axes(0x31, 0x31, 254, 7)]));
        assert!(!relative_axes(&[x, axes(0x31, 0x31, 255, 8)]));
        assert!(!relative_axes(&[axes(0x38, 0x38, 0, 0)]));
        assert!(!relative_axes(&[]));
        let mut single = x;
        single.IsRange = false;
        single.Anonymous.NotRange = HIDP_VALUE_CAPS_0_1 {
            Usage: 0x30,
            ..Default::default()
        };
        assert!(relative_axes(&[single, y]));
    }

    #[test]
    fn absolute_vendor_constant_array_and_invalid_values_do_not_prove_relative_motion() {
        let valid = axes(0x30, 0x31, 1, 1);
        for invalid in [
            HIDP_VALUE_CAPS {
                IsAbsolute: true,
                ..valid
            },
            HIDP_VALUE_CAPS {
                UsagePage: 0xff00,
                ..valid
            },
            HIDP_VALUE_CAPS {
                BitField: 0x03,
                ..valid
            },
            HIDP_VALUE_CAPS {
                BitField: 0x00,
                ..valid
            },
            HIDP_VALUE_CAPS {
                BitSize: 0,
                ..valid
            },
            HIDP_VALUE_CAPS {
                ReportCount: 0,
                ..valid
            },
            HIDP_VALUE_CAPS {
                LogicalMin: 32767,
                ..valid
            },
            axes(0x31, 0x30, 1, 1),
        ] {
            assert!(!relative_axes(&[invalid]));
        }
        assert!(relative_axes(&[valid]));
    }

    #[test]
    fn unavailable_hid_descriptor_is_unknown_not_confirmed_absence() {
        let path = r"\\?\HID#clicksync-invalid-descriptor";
        assert_eq!(relative_mouse(path), None);
        assert_eq!(typing_keyboard(path), None);
    }

    fn range(first: u16, last: u16, bits: u16, report: u8) -> HIDP_BUTTON_CAPS {
        HIDP_BUTTON_CAPS {
            UsagePage: KEYBOARD_KEYS,
            IsRange: true,
            BitField: bits,
            ReportID: report,
            Anonymous: HIDP_BUTTON_CAPS_0 {
                Range: HIDP_BUTTON_CAPS_0_0 {
                    UsageMin: first,
                    UsageMax: last,
                    ..Default::default()
                },
            },
            ..Default::default()
        }
    }

    #[test]
    fn independent_typing_keys_distinguish_full_keyboard_from_mouse_macro_arrays() {
        // GL68's NKRO collection: report 1, individual key bits 0x04..0x70.
        assert!(typing_matrix(&[range(0x04, 0x70, 0x02, 1)]));
        // VAXEE's macro collection: modifiers plus an array of arbitrary key usages.
        assert!(!typing_matrix(&[
            range(0xe0, 0xe7, 0x02, 2),
            range(0x00, 0xff, 0x00, 2)
        ]));
        assert!(!typing_matrix(&[range(0x04, 0x70, 0x03, 1)])); // constant padding
        assert!(!typing_matrix(&[range(0x04, 0x1d, 0x02, 1)])); // letters alone
        assert!(typing_matrix(&[
            range(0x04, 0x1d, 0x02, 1),
            range(0x1e, 0x70, 0x02, 1)
        ]));
        assert!(!typing_matrix(&[
            range(0x04, 0x1d, 0x02, 1),
            range(0x1e, 0x70, 0x02, 2)
        ]));
        let mut vendor = range(0x04, 0x70, 0x02, 1);
        vendor.UsagePage = 0xff00;
        assert!(!typing_matrix(&[vendor]));
        let space = HIDP_BUTTON_CAPS {
            UsagePage: KEYBOARD_KEYS,
            BitField: DATA_VARIABLE,
            ReportID: 255,
            Anonymous: HIDP_BUTTON_CAPS_0 {
                NotRange: HIDP_BUTTON_CAPS_0_1 {
                    Usage: 0x2c,
                    ..Default::default()
                },
            },
            ..Default::default()
        };
        assert!(typing_matrix(&[range(0x04, 0x28, 0x02, 255), space]));
        assert!(!typing_matrix(&[range(0x04, 0x28, 0x02, 255)]));
        assert!(!typing_matrix(&[range(0x70, 0x04, 0x02, 1)]));
        assert!(!typing_matrix(&[]));
    }
}
