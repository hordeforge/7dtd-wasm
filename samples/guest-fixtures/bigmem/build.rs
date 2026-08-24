//! Build script for the bigmem fixture.
//!
//! Declares a 128 MiB memory maximum, above the host cap (default 32 MiB),
//! so the host must reject this module at load time. The link argument is
//! emitted here rather than in the shared .cargo/config.toml so the override
//! is part of this crate's fingerprint.

fn main() {
    println!("cargo:rustc-link-arg=--max-memory=134217728");
}
