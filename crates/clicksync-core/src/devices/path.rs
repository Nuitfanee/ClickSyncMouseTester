/// Bounded, cold-path parsing shared by discovery and physical grouping.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DevicePath {
    pub vendor_id: Option<i32>,
    pub product_id: Option<i32>,
    pub physical_device_key: String,
    pub endpoint_token: String,
    /// Same HID input interface, retaining MI and the physical instance across collections.
    pub interface_key: String,
    pub summary: String,
}

const MOUSE_INTERFACE: &str = "#{378DE44C-56EF-11D1-BC8C-00A0C91405DD}";

impl DevicePath {
    pub fn parse(path: &str) -> Self {
        let normalized = normalize(path);
        let tokens: Vec<_> = normalized.split(['#', '\\', '&']).collect();
        let endpoint_tokens: Vec<_> = tokens
            .iter()
            .copied()
            .filter(|token| is_endpoint_token(token))
            .collect();
        let mut unique_tokens = Vec::new();
        for token in endpoint_tokens {
            if !unique_tokens.contains(&token) {
                unique_tokens.push(token);
            }
        }
        Self {
            vendor_id: hex_token(&tokens, "VID_"),
            product_id: hex_token(&tokens, "PID_"),
            physical_device_key: physical_path_key(&normalized),
            endpoint_token: unique_tokens.join(" / "),
            interface_key: collection_path_key(&normalized, false),
            summary: summary(path),
        }
    }
}

pub fn normalize(path: &str) -> String {
    let value = path.trim().trim_end_matches('\0').replace('/', "\\");
    let value = value
        .strip_prefix("\\??\\")
        .map_or_else(|| value.clone(), |suffix| format!("\\\\?\\{suffix}"));
    value.to_uppercase()
}

fn hex_token(tokens: &[&str], prefix: &str) -> Option<i32> {
    tokens.iter().find_map(|token| {
        let value = token.strip_prefix(prefix)?;
        (value.len() == 4)
            .then(|| i32::from_str_radix(value, 16).ok())
            .flatten()
    })
}

fn is_endpoint_token(token: &str) -> bool {
    ["MI_", "COL"].iter().any(|prefix| {
        token
            .strip_prefix(prefix)
            .is_some_and(|hex| hex.len() == 2 && hex.bytes().all(|ch| ch.is_ascii_hexdigit()))
    })
}

fn physical_path_key(path: &str) -> String {
    collection_path_key(path, true)
}

fn collection_path_key(path: &str, remove_interface: bool) -> String {
    // Mouse, keyboard and generic HID interfaces use different class GUIDs.
    let path = path
        .rsplit_once("#{")
        .filter(|(_, guid)| guid.ends_with('}'))
        .map_or(path, |(prefix, _)| prefix);
    let sections: Vec<_> = path.split('#').collect();
    sections
        .iter()
        .enumerate()
        .map(|(index, section)| {
            let mut tokens: Vec<_> = section
                .split('&')
                .filter(|token| {
                    !token.is_empty()
                        && !(is_endpoint_token(token)
                            && (remove_interface || token.starts_with("COL")))
                })
                .collect();
            // The collection suffix belongs to the endpoint, not to the USB instance.
            if index > 1
                && index + 1 == sections.len()
                && tokens.len() > 1
                && tokens.last().is_some_and(|suffix| {
                    suffix.len() == 4 && suffix.bytes().all(|ch| ch.is_ascii_hexdigit())
                })
            {
                tokens.pop();
            }
            tokens.join("&")
        })
        .filter(|section| !section.is_empty())
        .collect::<Vec<_>>()
        .join("#")
}

fn summary(path: &str) -> String {
    let value = path.trim().trim_end_matches('\0').replace("\\\\?\\", "");
    let value = if value.to_uppercase().ends_with(MOUSE_INTERFACE) {
        &value[..value.len() - MOUSE_INTERFACE.len()]
    } else {
        &value
    };
    if value.chars().count() <= 48 {
        value.into()
    } else {
        let prefix: String = value.chars().take(24).collect();
        let suffix: String = value
            .chars()
            .rev()
            .take(18)
            .collect::<String>()
            .chars()
            .rev()
            .collect();
        format!("{prefix}...{suffix}")
    }
}
