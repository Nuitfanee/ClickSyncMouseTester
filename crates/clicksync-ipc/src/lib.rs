//! Bounded, cancellable Windows named-pipe I/O shared by app and capture.
//!
//! This is a byte transport, not a framing layer. Errors can follow partial I/O;
//! callers own framing, peer verification, and separate control/data pipes.

#![deny(unsafe_op_in_unsafe_fn)]

pub use platform::Pipe;

#[cfg(windows)]
mod platform {
    use std::cell::UnsafeCell;
    use std::ffi::c_void;
    use std::io::{self, ErrorKind};
    use std::mem::size_of;
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr::{null, null_mut};
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, Mutex, MutexGuard, TryLockError};
    use std::thread;
    use std::time::{Duration, Instant};

    use windows_sys::Win32::Foundation::{
        ERROR_FILE_NOT_FOUND, ERROR_INSUFFICIENT_BUFFER, ERROR_IO_INCOMPLETE, ERROR_IO_PENDING,
        ERROR_NOT_FOUND, ERROR_OPERATION_ABORTED, ERROR_PIPE_BUSY, ERROR_PIPE_CONNECTED,
        GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE, LocalFree, WAIT_TIMEOUT,
    };
    use windows_sys::Win32::Security::Authorization::{
        ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
        SDDL_REVISION_1,
    };
    use windows_sys::Win32::Security::{
        GetTokenInformation, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER, TokenUser,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, OPEN_EXISTING,
        PIPE_ACCESS_DUPLEX, ReadFile, SECURITY_IDENTIFICATION, SECURITY_SQOS_PRESENT, WriteFile,
    };
    use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResultEx, OVERLAPPED};
    use windows_sys::Win32::System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, GetNamedPipeClientProcessId,
        GetNamedPipeServerProcessId, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS,
        PIPE_TYPE_BYTE, PIPE_WAIT,
    };
    use windows_sys::Win32::System::Threading::{
        CreateEventW, GetCurrentProcess, OpenProcessToken, ResetEvent,
    };

    const PREFIX: &str = r"\\.\pipe\ClickSync-";
    const MAX_NAME_UNITS: usize = 200;
    const PIPE_BUFFER_BYTES: u32 = 32 * 1024;
    const IO_CHUNK_BYTES: usize = 16 * 1024;
    const WAIT_SLICE: Duration = Duration::from_millis(20);

    /// A single duplex byte pipe. Clones share ownership, not a global I/O lock.
    #[derive(Clone)]
    pub struct Pipe {
        inner: Arc<Inner>,
    }

    struct Inner {
        handle: OwnedHandle,
        server: bool,
        connected: Mutex<bool>,
        read: Mutex<()>,
        write: Mutex<()>,
    }

    impl Pipe {
        /// Creates the first, local-only instance, accessible only to the
        /// process token's user and SYSTEM. Connection is a separate operation.
        pub fn server(name: &str) -> io::Result<Self> {
            let name = pipe_name(name)?;
            let descriptor = security_descriptor()?;
            let attributes = SECURITY_ATTRIBUTES {
                nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor.0,
                bInheritHandle: 0,
            };
            let handle = own_handle(unsafe {
                CreateNamedPipeW(
                    name.as_ptr(),
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                    1,
                    PIPE_BUFFER_BYTES,
                    PIPE_BUFFER_BYTES,
                    WAIT_SLICE.as_millis() as u32,
                    &attributes,
                )
            })?;
            Ok(Self::from_handle(handle, true))
        }

        /// Opens a local pipe, retrying only missing/busy instances with a
        /// bounded sleep. The server cannot impersonate the client above
        /// identification level.
        pub fn client(name: &str, deadline: Instant, cancel: &AtomicBool) -> io::Result<Self> {
            let name = pipe_name(name)?;
            loop {
                check(deadline, cancel)?;
                let handle = unsafe {
                    CreateFileW(
                        name.as_ptr(),
                        GENERIC_READ | GENERIC_WRITE,
                        0,
                        null(),
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                        null_mut(),
                    )
                };
                if handle != INVALID_HANDLE_VALUE {
                    let pipe = Self::from_handle(own_handle(handle)?, false);
                    check(deadline, cancel)?;
                    return Ok(pipe);
                }
                let error = io::Error::last_os_error();
                match error.raw_os_error().map(|code| code as u32) {
                    Some(ERROR_FILE_NOT_FOUND | ERROR_PIPE_BUSY) => sleep_slice(deadline, cancel)?,
                    _ => return Err(error),
                }
            }
        }

        /// Accepts the client on a server pipe. Already-connected pipes,
        /// including client handles, need no further handshake.
        pub fn connect(&self, deadline: Instant, cancel: &AtomicBool) -> io::Result<()> {
            let mut connected = lock(&self.inner.connected, deadline, cancel)?;
            if !*connected {
                let mut operation = Operation::new(Arc::clone(&self.inner), Kind::Connect, 0)?;
                operation.run(0, deadline, cancel)?;
                *connected = true;
            }
            check(deadline, cancel)
        }

        /// Reads exactly the requested bytes. A timeout/error does not undo
        /// bytes already consumed. Each kernel request is at most 16 KiB.
        pub fn read_exact(
            &self,
            mut buf: &mut [u8],
            deadline: Instant,
            cancel: &AtomicBool,
        ) -> io::Result<()> {
            check(deadline, cancel)?;
            if buf.is_empty() {
                return Ok(());
            }
            let _reader = lock(&self.inner.read, deadline, cancel)?;
            let mut operation = Operation::new(
                Arc::clone(&self.inner),
                Kind::Read,
                buf.len().min(IO_CHUNK_BYTES),
            )?;
            while !buf.is_empty() {
                let length = buf.len().min(operation.buffer.len());
                let count = operation.run(length, deadline, cancel)? as usize;
                if count == 0 {
                    return Err(io::Error::new(ErrorKind::UnexpectedEof, "pipe reached EOF"));
                }
                if count > length {
                    return Err(io::Error::new(
                        ErrorKind::InvalidData,
                        "invalid read length",
                    ));
                }
                buf[..count].copy_from_slice(&operation.buffer[..count]);
                buf = &mut buf[count..];
            }
            Ok(())
        }

        /// Writes all requested bytes with at most 16 KiB of owned staging.
        /// Concurrent writers are serialized separately from readers.
        pub fn write_all(
            &self,
            mut buf: &[u8],
            deadline: Instant,
            cancel: &AtomicBool,
        ) -> io::Result<()> {
            check(deadline, cancel)?;
            if buf.is_empty() {
                return Ok(());
            }
            let _writer = lock(&self.inner.write, deadline, cancel)?;
            let mut operation = Operation::new(
                Arc::clone(&self.inner),
                Kind::Write,
                buf.len().min(IO_CHUNK_BYTES),
            )?;
            while !buf.is_empty() {
                check(deadline, cancel)?;
                let length = buf.len().min(operation.buffer.len());
                operation.buffer[..length].copy_from_slice(&buf[..length]);
                let count = operation.run(length, deadline, cancel)? as usize;
                if count == 0 {
                    return Err(io::Error::new(
                        ErrorKind::WriteZero,
                        "pipe wrote zero bytes",
                    ));
                }
                if count > length {
                    return Err(io::Error::new(
                        ErrorKind::InvalidData,
                        "invalid write length",
                    ));
                }
                buf = &buf[count..];
            }
            Ok(())
        }

        /// Requests cancellation of currently pending I/O, not future calls.
        /// Each operation retains its resources until cancellation completes.
        pub fn cancel(&self) -> io::Result<()> {
            if unsafe { CancelIoEx(self.inner.handle.as_raw_handle(), null()) } != 0 {
                return Ok(());
            }
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NOT_FOUND as i32) {
                Ok(())
            } else {
                Err(error)
            }
        }

        /// Returns the connected peer's actual Windows process ID.
        pub fn peer_process_id(&self) -> io::Result<u32> {
            let mut pid = 0;
            let result = unsafe {
                if self.inner.server {
                    GetNamedPipeClientProcessId(self.inner.handle.as_raw_handle(), &mut pid)
                } else {
                    GetNamedPipeServerProcessId(self.inner.handle.as_raw_handle(), &mut pid)
                }
            };
            if result == 0 {
                Err(io::Error::last_os_error())
            } else {
                Ok(pid)
            }
        }

        fn from_handle(handle: OwnedHandle, server: bool) -> Self {
            Self {
                inner: Arc::new(Inner {
                    handle,
                    server,
                    connected: Mutex::new(!server),
                    read: Mutex::new(()),
                    write: Mutex::new(()),
                }),
            }
        }
    }

    impl Drop for Inner {
        fn drop(&mut self) {
            // Pending operations own an Arc, so none can survive this last
            // owner. OwnedHandle closes the handle after this cancellation.
            unsafe { CancelIoEx(self.handle.as_raw_handle(), null()) };
        }
    }

    #[derive(Clone, Copy)]
    enum Kind {
        Connect,
        Read,
        Write,
    }

    struct Operation {
        pipe: Arc<Inner>,
        event: OwnedHandle,
        // The kernel updates this independently of Rust's operation metadata.
        // The heap address stays stable until a terminal completion is observed.
        overlapped: Box<UnsafeCell<OVERLAPPED>>,
        buffer: Vec<u8>,
        kind: Kind,
        pending: bool,
    }

    impl Operation {
        fn new(pipe: Arc<Inner>, kind: Kind, capacity: usize) -> io::Result<Self> {
            let event = own_handle(unsafe { CreateEventW(null(), 1, 0, null()) })?;
            Ok(Self {
                pipe,
                event,
                overlapped: Box::new(UnsafeCell::new(OVERLAPPED::default())),
                buffer: vec![0; capacity],
                kind,
                pending: false,
            })
        }

        fn run(
            &mut self,
            length: usize,
            deadline: Instant,
            cancel: &AtomicBool,
        ) -> io::Result<u32> {
            check(deadline, cancel)?;
            if unsafe { ResetEvent(self.event.as_raw_handle()) } == 0 {
                return Err(io::Error::last_os_error());
            }
            // Reuse is permitted only after the previous request completed.
            unsafe {
                self.overlapped.get().write(OVERLAPPED {
                    hEvent: self.event.as_raw_handle(),
                    ..OVERLAPPED::default()
                });
            }
            check(deadline, cancel)?;
            self.pending = true;
            let started = unsafe {
                match self.kind {
                    Kind::Connect => {
                        ConnectNamedPipe(self.pipe.handle.as_raw_handle(), self.overlapped.get())
                    }
                    Kind::Read => ReadFile(
                        self.pipe.handle.as_raw_handle(),
                        self.buffer.as_mut_ptr(),
                        length as u32,
                        null_mut(),
                        self.overlapped.get(),
                    ),
                    Kind::Write => WriteFile(
                        self.pipe.handle.as_raw_handle(),
                        self.buffer.as_ptr(),
                        length as u32,
                        null_mut(),
                        self.overlapped.get(),
                    ),
                }
            };
            if started == 0 {
                let error = io::Error::last_os_error();
                match error.raw_os_error().map(|code| code as u32) {
                    Some(ERROR_IO_PENDING) => {}
                    Some(ERROR_PIPE_CONNECTED) if matches!(self.kind, Kind::Connect) => {
                        self.pending = false;
                        check(deadline, cancel)?;
                        return Ok(0);
                    }
                    _ => {
                        self.pending = false;
                        return Err(io_error(error));
                    }
                }
            }
            loop {
                if let Err(error) = check(deadline, cancel) {
                    self.cancel_and_drain();
                    return Err(error);
                }
                match self.poll(wait_millis(deadline)) {
                    Ok(Some(count)) => {
                        check(deadline, cancel)?;
                        return Ok(count);
                    }
                    Ok(None) => {}
                    Err(error) => return Err(error),
                }
            }
        }

        fn poll(&mut self, millis: u32) -> io::Result<Option<u32>> {
            let mut count = 0;
            if unsafe {
                GetOverlappedResultEx(
                    self.pipe.handle.as_raw_handle(),
                    self.overlapped.get(),
                    &mut count,
                    millis,
                    0,
                )
            } != 0
            {
                self.pending = false;
                return Ok(Some(count));
            }
            let error = io::Error::last_os_error();
            match error.raw_os_error().map(|code| code as u32) {
                Some(ERROR_IO_INCOMPLETE | WAIT_TIMEOUT) => Ok(None),
                _ => {
                    self.pending = false;
                    Err(io_error(error))
                }
            }
        }

        fn cancel_and_drain(&mut self) {
            if self.pending {
                unsafe {
                    CancelIoEx(self.pipe.handle.as_raw_handle(), self.overlapped.get());
                }
                // Cancellation is only a request, including ERROR_NOT_FOUND.
                // Do not release/reuse the owned buffer, event or OVERLAPPED
                // before terminal completion. This waits; it never reissues I/O.
                while self.pending {
                    let _ = self.poll(WAIT_SLICE.as_millis() as u32);
                }
            }
        }
    }

    impl Drop for Operation {
        fn drop(&mut self) {
            self.cancel_and_drain();
        }
    }

    fn check(deadline: Instant, cancel: &AtomicBool) -> io::Result<()> {
        if cancel.load(Ordering::Acquire) {
            Err(io::Error::new(
                ErrorKind::Interrupted,
                "pipe operation cancelled",
            ))
        } else if Instant::now() >= deadline {
            Err(io::Error::new(
                ErrorKind::TimedOut,
                "pipe operation timed out",
            ))
        } else {
            Ok(())
        }
    }

    fn wait_millis(deadline: Instant) -> u32 {
        deadline
            .saturating_duration_since(Instant::now())
            .min(WAIT_SLICE)
            .as_nanos()
            .div_ceil(1_000_000)
            .max(1) as u32
    }

    fn sleep_slice(deadline: Instant, cancel: &AtomicBool) -> io::Result<()> {
        check(deadline, cancel)?;
        thread::sleep(
            deadline
                .saturating_duration_since(Instant::now())
                .min(WAIT_SLICE),
        );
        check(deadline, cancel)
    }

    fn lock<'a, T>(
        mutex: &'a Mutex<T>,
        deadline: Instant,
        cancel: &AtomicBool,
    ) -> io::Result<MutexGuard<'a, T>> {
        loop {
            check(deadline, cancel)?;
            match mutex.try_lock() {
                Ok(guard) => return Ok(guard),
                Err(TryLockError::WouldBlock) => sleep_slice(deadline, cancel)?,
                Err(TryLockError::Poisoned(_)) => {
                    return Err(io::Error::other("pipe lock poisoned"));
                }
            }
        }
    }

    fn io_error(error: io::Error) -> io::Error {
        if error.raw_os_error() == Some(ERROR_OPERATION_ABORTED as i32) {
            io::Error::new(ErrorKind::Interrupted, error)
        } else {
            error
        }
    }

    fn pipe_name(name: &str) -> io::Result<Vec<u16>> {
        let suffix = name.strip_prefix(PREFIX).ok_or_else(|| {
            io::Error::new(ErrorKind::InvalidInput, "invalid ClickSync pipe prefix")
        })?;
        let mut wide: Vec<u16> = name.encode_utf16().take(MAX_NAME_UNITS + 1).collect();
        if wide.len() > MAX_NAME_UNITS || suffix.is_empty() || suffix.contains(['\\', '\0']) {
            return Err(io::Error::new(
                ErrorKind::InvalidInput,
                "invalid ClickSync pipe name",
            ));
        }
        wide.push(0);
        Ok(wide)
    }

    fn own_handle(handle: HANDLE) -> io::Result<OwnedHandle> {
        if handle.is_null() || handle == INVALID_HANDLE_VALUE {
            Err(io::Error::last_os_error())
        } else {
            Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
        }
    }

    struct LocalMemory(*mut c_void);

    impl Drop for LocalMemory {
        fn drop(&mut self) {
            unsafe { LocalFree(self.0) };
        }
    }

    fn current_user_sid() -> io::Result<String> {
        let mut raw = null_mut();
        if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut raw) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let token = own_handle(raw)?;
        let mut required = 0;
        let result = unsafe {
            GetTokenInformation(
                token.as_raw_handle(),
                TokenUser,
                null_mut(),
                0,
                &mut required,
            )
        };
        let error = io::Error::last_os_error();
        if result != 0 || error.raw_os_error() != Some(ERROR_INSUFFICIENT_BUFFER as i32) {
            return Err(error);
        }
        if required < size_of::<TOKEN_USER>() as u32 || required > 64 * 1024 {
            return Err(io::Error::new(
                ErrorKind::InvalidData,
                "invalid token user size",
            ));
        }
        // TOKEN_USER contains a pointer, so byte-aligned allocation is not enough.
        let mut storage = vec![0usize; (required as usize).div_ceil(size_of::<usize>())];
        if unsafe {
            GetTokenInformation(
                token.as_raw_handle(),
                TokenUser,
                storage.as_mut_ptr().cast(),
                required,
                &mut required,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        let sid = unsafe { (*storage.as_ptr().cast::<TOKEN_USER>()).User.Sid };
        let mut text = null_mut();
        if unsafe { ConvertSidToStringSidW(sid, &mut text) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let _allocation = LocalMemory(text.cast());
        let mut length = 0;
        while unsafe { *text.add(length) } != 0 {
            length += 1;
        }
        String::from_utf16(unsafe { std::slice::from_raw_parts(text, length) })
            .map_err(|_| io::Error::new(ErrorKind::InvalidData, "invalid user SID string"))
    }

    fn security_descriptor() -> io::Result<LocalMemory> {
        let user = current_user_sid()?;
        let sddl: Vec<u16> = format!("D:P(A;;GA;;;SY)(A;;GA;;;{user})")
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let mut descriptor = null_mut();
        if unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                null_mut(),
            )
        } == 0
        {
            Err(io::Error::last_os_error())
        } else {
            Ok(LocalMemory(descriptor))
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use windows_sys::Win32::Foundation::GENERIC_ALL;
        use windows_sys::Win32::Security::Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT};
        use windows_sys::Win32::Security::{ACCESS_ALLOWED_ACE, DACL_SECURITY_INFORMATION, GetAce};
        use windows_sys::Win32::Storage::FileSystem::FILE_ALL_ACCESS;
        use windows_sys::Win32::System::Pipes::{GetNamedPipeInfo, PIPE_TYPE_MESSAGE};

        #[test]
        fn server_dacl_contains_only_process_user_and_system() {
            let pipe = Pipe::server(&format!("{PREFIX}dacl-{}", std::process::id())).unwrap();
            let mut descriptor = null_mut();
            let mut acl = null_mut();
            assert_eq!(
                unsafe {
                    GetSecurityInfo(
                        pipe.inner.handle.as_raw_handle(),
                        SE_KERNEL_OBJECT,
                        DACL_SECURITY_INFORMATION,
                        null_mut(),
                        null_mut(),
                        &mut acl,
                        null_mut(),
                        &mut descriptor,
                    )
                },
                0
            );
            let _allocation = LocalMemory(descriptor);
            assert!(!acl.is_null());
            assert_eq!(unsafe { (*acl).AceCount }, 2);
            let mut principals = Vec::new();
            for index in 0..2 {
                let mut ace = null_mut();
                assert_ne!(unsafe { GetAce(acl, index, &mut ace) }, 0);
                let ace = ace.cast::<ACCESS_ALLOWED_ACE>();
                assert_eq!(unsafe { (*ace).Header.AceType }, 0);
                assert_eq!(unsafe { (*ace).Header.AceFlags }, 0);
                let mask = unsafe { (*ace).Mask };
                assert!(mask == GENERIC_ALL || mask == FILE_ALL_ACCESS);
                let sid = unsafe { std::ptr::addr_of_mut!((*ace).SidStart) }.cast();
                let mut text = null_mut();
                assert_ne!(unsafe { ConvertSidToStringSidW(sid, &mut text) }, 0);
                let _sid_allocation = LocalMemory(text.cast());
                let mut length = 0;
                while unsafe { *text.add(length) } != 0 {
                    length += 1;
                }
                principals.push(
                    String::from_utf16(unsafe { std::slice::from_raw_parts(text, length) })
                        .unwrap(),
                );
            }
            let mut expected = vec![current_user_sid().unwrap(), "S-1-5-18".to_owned()];
            expected.sort();
            principals.sort();
            assert_eq!(principals, expected);
        }

        #[test]
        fn server_is_a_single_byte_pipe_with_bounded_buffers() {
            let pipe = Pipe::server(&format!("{PREFIX}limits-{}", std::process::id())).unwrap();
            let mut flags = 0;
            let mut output = 0;
            let mut input = 0;
            let mut instances = 0;
            assert_ne!(
                unsafe {
                    GetNamedPipeInfo(
                        pipe.inner.handle.as_raw_handle(),
                        &mut flags,
                        &mut output,
                        &mut input,
                        &mut instances,
                    )
                },
                0
            );
            assert_eq!(flags & PIPE_TYPE_MESSAGE, 0);
            assert_eq!(instances, 1);
            assert!(output > 0 && input > 0);
            assert!(u64::from(output) + u64::from(input) <= 64 * 1024);
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use std::io;
    use std::sync::atomic::AtomicBool;
    use std::time::Instant;

    #[derive(Clone)]
    pub struct Pipe;

    fn unsupported<T>() -> io::Result<T> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "ClickSync named pipes require Windows",
        ))
    }

    impl Pipe {
        pub fn server(_name: &str) -> io::Result<Self> {
            unsupported()
        }

        pub fn client(_name: &str, _deadline: Instant, _cancel: &AtomicBool) -> io::Result<Self> {
            unsupported()
        }

        pub fn connect(&self, _deadline: Instant, _cancel: &AtomicBool) -> io::Result<()> {
            unsupported()
        }

        pub fn read_exact(
            &self,
            _buf: &mut [u8],
            _deadline: Instant,
            _cancel: &AtomicBool,
        ) -> io::Result<()> {
            unsupported()
        }

        pub fn write_all(
            &self,
            _buf: &[u8],
            _deadline: Instant,
            _cancel: &AtomicBool,
        ) -> io::Result<()> {
            unsupported()
        }

        pub fn cancel(&self) -> io::Result<()> {
            unsupported()
        }

        pub fn peer_process_id(&self) -> io::Result<u32> {
            unsupported()
        }
    }
}
