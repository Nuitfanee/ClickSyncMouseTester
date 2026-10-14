use std::fmt;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

#[derive(Debug, Eq, PartialEq)]
pub struct BudgetExceeded {
    pub requested_bytes: usize,
    pub available_bytes: usize,
    pub limit_bytes: usize,
}

impl fmt::Display for BudgetExceeded {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "managed byte budget exceeded: requested {}, available {}, limit {}",
            self.requested_bytes, self.available_bytes, self.limit_bytes
        )
    }
}

impl std::error::Error for BudgetExceeded {}

pub struct SharedByteBudget {
    limit_bytes: usize,
    reserved_bytes: AtomicUsize,
}

impl SharedByteBudget {
    pub fn new(limit_bytes: usize) -> Arc<Self> {
        Arc::new(Self {
            limit_bytes,
            reserved_bytes: AtomicUsize::new(0),
        })
    }

    pub fn limit_bytes(&self) -> usize {
        self.limit_bytes
    }

    pub fn reserved_bytes(&self) -> usize {
        self.reserved_bytes.load(Ordering::Acquire)
    }

    pub fn available_bytes(&self) -> usize {
        self.limit_bytes.saturating_sub(self.reserved_bytes())
    }

    pub fn try_reserve(
        self: &Arc<Self>,
        requested_bytes: usize,
    ) -> Result<BudgetReservation, BudgetExceeded> {
        if requested_bytes == 0 {
            return Ok(BudgetReservation {
                budget: Arc::clone(self),
                bytes: 0,
            });
        }

        let mut current = self.reserved_bytes.load(Ordering::Acquire);
        loop {
            let Some(next) = current.checked_add(requested_bytes) else {
                return Err(BudgetExceeded {
                    requested_bytes,
                    available_bytes: 0,
                    limit_bytes: self.limit_bytes,
                });
            };
            if next > self.limit_bytes {
                return Err(BudgetExceeded {
                    requested_bytes,
                    available_bytes: self.limit_bytes.saturating_sub(current),
                    limit_bytes: self.limit_bytes,
                });
            }
            match self.reserved_bytes.compare_exchange_weak(
                current,
                next,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => {
                    return Ok(BudgetReservation {
                        budget: Arc::clone(self),
                        bytes: requested_bytes,
                    });
                }
                Err(observed) => current = observed,
            }
        }
    }
}

pub struct BudgetReservation {
    budget: Arc<SharedByteBudget>,
    bytes: usize,
}

impl BudgetReservation {
    pub fn bytes(&self) -> usize {
        self.bytes
    }
}

impl Drop for BudgetReservation {
    fn drop(&mut self) {
        if self.bytes != 0 {
            self.budget
                .reserved_bytes
                .fetch_sub(self.bytes, Ordering::AcqRel);
        }
    }
}
