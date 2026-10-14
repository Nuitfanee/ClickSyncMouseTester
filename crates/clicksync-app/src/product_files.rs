use crate::{backend::SessionPrefix, chart::ChartResult, product::Settings};
use clicksync_core::{SessionConfig, SessionSnapshot, SharedByteBudget};
use serde::Serialize;
use std::{
    fs::{File, OpenOptions},
    io::{BufReader, BufWriter, Write},
    path::{Path, PathBuf},
    sync::{Arc, mpsc},
    thread::{self, JoinHandle},
    time::Duration,
};

pub enum FileRequest {
    ImportAnalysis {
        path: PathBuf,
        operation: u64,
        budget: Arc<SharedByteBudget>,
        existing: Vec<(Arc<SessionSnapshot>, Option<f64>)>,
    },
    ConvertRange {
        snapshots: Vec<Arc<SessionSnapshot>>,
        range: (f64, f64),
        logical: bool,
        operation: u64,
    },
    Export {
        path: PathBuf,
        snapshot: Arc<SessionSnapshot>,
        cpi: Option<f64>,
    },
    Query {
        result: Arc<ChartResult>,
        key: u32,
        range: std::ops::Range<usize>,
    },
    Settings(Settings),
}
#[derive(Clone, Serialize)]
pub struct QuerySummary {
    pub request_id: u64,
    pub key: u32,
    pub generation: u64,
    pub end: usize,
    pub range: std::ops::Range<usize>,
    pub record_count: usize,
    pub distribution: Option<clicksync_chart::analysis::statistics::Distribution>,
    pub residual_distribution: Option<clicksync_chart::analysis::statistics::Distribution>,
    pub sum_x: i64,
    pub sum_y: i64,
}
pub enum FileResult {
    ImportedAnalysis {
        group: crate::product::performance_analysis::Group,
        operation: u64,
    },
    Duplicate {
        operation: u64,
    },
    ConvertedRange {
        operation: u64,
        range: Option<(f64, f64)>,
    },
    Saved(PathBuf),
    Query(QuerySummary),
}

pub struct FileWorker {
    sender: Option<mpsc::SyncSender<FileRequest>>,
    receiver: mpsc::Receiver<Result<FileResult, String>>,
    thread: Option<JoinHandle<()>>,
}
impl FileWorker {
    pub fn spawn() -> Self {
        let (sender, requests) = mpsc::sync_channel(1);
        let (results, receiver) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            while let Ok(request) = requests.recv() {
                let result = execute(request);
                if results.send(result).is_err() {
                    break;
                }
            }
        });
        Self {
            sender: Some(sender),
            receiver,
            thread: Some(thread),
        }
    }
    pub fn submit(&self, request: FileRequest) -> Result<(), String> {
        self.sender
            .as_ref()
            .ok_or("file worker closed")?
            .try_send(request)
            .map_err(|e| e.to_string())
    }
    pub fn take(&self) -> Option<Result<FileResult, String>> {
        self.receiver.try_recv().ok()
    }
    /// Finish fixed-input operations and the latest unsaved preference value.
    pub fn finish(&mut self, settings: Option<Settings>) -> Result<(), String> {
        let mut failure = None;
        if let Some(settings) = settings
            && let Some(sender) = &self.sender
        {
            let mut request = FileRequest::Settings(settings);
            loop {
                match sender.try_send(request) {
                    Ok(()) => break,
                    Err(mpsc::TrySendError::Full(pending)) => {
                        request = pending;
                        if let Ok(Err(error)) =
                            self.receiver.recv_timeout(Duration::from_millis(10))
                        {
                            failure.get_or_insert(error);
                        }
                    }
                    Err(mpsc::TrySendError::Disconnected(_)) => {
                        failure
                            .get_or_insert("file worker disconnected before settings save".into());
                        break;
                    }
                }
            }
        }
        self.sender.take();
        if let Some(thread) = self.thread.take() {
            while !thread.is_finished() {
                if let Ok(Err(error)) = self.receiver.recv_timeout(Duration::from_millis(10)) {
                    failure.get_or_insert(error);
                }
            }
            while let Ok(result) = self.receiver.try_recv() {
                if let Err(error) = result {
                    failure.get_or_insert(error);
                }
            }
            if thread.join().is_err() {
                failure.get_or_insert("file worker panicked".into());
            }
        }
        failure.map_or(Ok(()), Err)
    }
}
impl Drop for FileWorker {
    fn drop(&mut self) {
        let _ = self.finish(None);
    }
}
pub fn execute(request: FileRequest) -> Result<FileResult, String> {
    match request {
        FileRequest::ConvertRange {
            snapshots,
            range,
            logical,
            operation,
        } => Ok(FileResult::ConvertedRange {
            operation,
            range: crate::product::performance_analysis::convert_group_range(
                &snapshots.iter().map(Arc::as_ref).collect::<Vec<_>>(),
                range,
                logical,
            ),
        }),
        FileRequest::ImportAnalysis {
            path,
            operation,
            budget,
            existing,
        } => {
            let imported = clicksync_core::exchange::import_reader(
                BufReader::new(File::open(path).map_err(|error| error.to_string())?),
                SessionConfig::default(),
                budget,
            )
            .map_err(|error| error.to_string())?;
            let snapshot = Arc::new(imported.snapshot);
            let cpi = imported.effective_cpi;
            if existing.iter().any(|(previous, previous_cpi)| {
                crate::product::performance_analysis::same_group(
                    &snapshot,
                    cpi,
                    previous,
                    *previous_cpi,
                )
            }) {
                return Ok(FileResult::Duplicate { operation });
            }
            let identity = crate::product::performance_analysis::group_identity(&snapshot, cpi);
            Ok(FileResult::ImportedAnalysis {
                group: crate::product::performance_analysis::Group {
                    key: 0,
                    snapshot,
                    cpi,
                    identity,
                },
                operation,
            })
        }
        FileRequest::Export {
            path,
            snapshot,
            cpi,
        } => {
            atomic_file(&path, |file| {
                let mut writer = BufWriter::with_capacity(65536, file);
                clicksync_core::exchange::export_writer(&mut writer, &snapshot, cpi, &utc_now())
                    .map_err(|e| e.to_string())?;
                writer.flush().map_err(|e| e.to_string())
            })?;
            Ok(FileResult::Saved(path))
        }
        FileRequest::Settings(mut settings) => {
            settings.validate();
            let path = Settings::path();
            std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
            atomic_file(&path, |file| {
                serde_json::to_writer(file, &settings).map_err(|e| e.to_string())
            })?;
            Ok(FileResult::Saved(path))
        }
        FileRequest::Query { result, key, range } => {
            let _reservation = result
                .byte_budget
                .as_ref()
                .map(|budget| {
                    let count = result
                        .snapshot(key)?
                        .event_count()
                        .max(4096)
                        .checked_next_power_of_two()
                        .ok_or("query capacity overflow")?;
                    let unit = std::mem::size_of::<clicksync_core::SessionEvent>() * 2
                        + std::mem::size_of::<clicksync_chart::Sample>() * 12
                        + 512;
                    let bytes = count.checked_mul(unit).ok_or("query capacity overflow")?;
                    budget.try_reserve(bytes).map_err(|error| error.to_string())
                })
                .transpose()?;
            let mut query = result.query(key, range.clone())?;
            let parameters = result.request.parameters_for(key);
            let selected_range = result
                .request
                .analysis
                .as_ref()
                .and_then(|analysis| analysis.range);
            crate::product::performance_analysis::filter_query(
                &mut query,
                selected_range,
                parameters.time_basis,
                result.snapshot(key)?.timestamp_frequency(),
            );
            let (distribution, residual_distribution) =
                if let Some(mut secondary) = result.query_secondary(key, range.clone())? {
                    crate::product::performance_analysis::filter_query(
                        &mut secondary,
                        selected_range,
                        parameters.time_basis,
                        result.snapshot(key)?.timestamp_frequency(),
                    );
                    let mut secondary_parameters = parameters.clone();
                    secondary_parameters.metric = result.request.secondary_metric.unwrap();
                    (
                        clicksync_chart::analysis::statistics::summarize_pair(
                            &query,
                            &secondary,
                            &parameters,
                        ),
                        clicksync_chart::analysis::statistics::residual_distribution_pair(
                            &query,
                            &secondary,
                            &parameters,
                            &secondary_parameters,
                        ),
                    )
                } else {
                    (
                        clicksync_chart::analysis::statistics::summarize(&query, &parameters),
                        clicksync_chart::analysis::statistics::residual_distribution(
                            &query,
                            &parameters,
                        ),
                    )
                };
            Ok(FileResult::Query(QuerySummary {
                request_id: result.request.id,
                key,
                generation: query.data_version.generation,
                end: query.data_version.end,
                range,
                record_count: query.records.len(),
                distribution,
                residual_distribution,
                sum_x: query.records.iter().map(|e| e.delta_x as i64).sum(),
                sum_y: query.records.iter().map(|e| e.delta_y as i64).sum(),
            }))
        }
    }
}
pub fn imported_prefix(
    snapshot: Arc<SessionSnapshot>,
    key: u32,
    identity: u64,
) -> Arc<SessionPrefix> {
    Arc::new(SessionPrefix {
        generation: identity,
        received_sequence: snapshot.event_count() as u64,
        received_records: snapshot.event_count() as u64,
        phase: crate::backend::CapturePhase::Stopped,
        complete: snapshot.integrity() == clicksync_core::session::CaptureIntegrity::Complete,
        datasets: vec![(key, snapshot)],
    })
}
pub fn atomic_file(
    path: &Path,
    write: impl FnOnce(&mut File) -> Result<(), String>,
) -> Result<(), String> {
    let parent = path.parent().ok_or("invalid destination")?;
    let temporary = parent.join(format!(
        ".clicksync-{}-{}.tmp",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|e| e.to_string())?
            .as_nanos()
    ));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|e| e.to_string())?;
        write(&mut file)?;
        file.sync_all().map_err(|e| e.to_string())?;
        drop(file);
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            use windows::Win32::Storage::FileSystem::{
                MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
            };
            let from: Vec<u16> = temporary.as_os_str().encode_wide().chain([0]).collect();
            let to: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
            unsafe {
                MoveFileExW(
                    windows::core::PCWSTR(from.as_ptr()),
                    windows::core::PCWSTR(to.as_ptr()),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
                )
            }
            .map_err(|e| e.to_string())?;
        }
        #[cfg(not(windows))]
        std::fs::rename(&temporary, path).map_err(|e| e.to_string())?;
        Ok(())
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}
fn utc_now() -> String {
    let time = unsafe { windows::Win32::System::SystemInformation::GetSystemTime() };
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z",
        time.wYear,
        time.wMonth,
        time.wDay,
        time.wHour,
        time.wMinute,
        time.wSecond,
        time.wMilliseconds
    )
}
