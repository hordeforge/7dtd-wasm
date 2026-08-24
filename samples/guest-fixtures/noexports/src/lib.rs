//! Test fixture: exports nothing from the hordeforge mod contract. The host
//! must reject it with a missing-export load error.

use guest_common as abi;

/// Not part of the mod contract; present only to prove the module compiles
/// and runs, while still missing the required exports.
#[export_name = "irrelevant_helper"]
pub extern "C" fn irrelevant_helper() -> i32 {
    abi::STATUS_OK
}
