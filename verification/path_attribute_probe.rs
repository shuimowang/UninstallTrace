use std::{env, ffi::OsStr, fs, os::windows::ffi::OsStrExt, time::Instant};

#[link(name = "kernel32")]
extern "system" {
    fn GetFileAttributesW(path: *const u16) -> u32;
}

fn main() {
    let manifest = env::args_os().nth(1).expect("expected a UTF-8 path manifest");
    let text = fs::read_to_string(manifest).expect("cannot read manifest");
    let paths: Vec<Vec<u16>> = text.lines().map(|line| {
        OsStr::new(line).encode_wide().chain(std::iter::once(0)).collect()
    }).collect();
    for round in 0..20 {
        let mut sum = 0u64;
        let start = Instant::now();
        for _ in 0..8 {
            for path in &paths {
                // The owned UTF-16 buffer stays alive for the call and ends with a NUL.
                sum += unsafe { GetFileAttributesW(path.as_ptr()) } as u64;
            }
        }
        let ms = start.elapsed().as_secs_f64() * 1_000.0;
        if round >= 5 {
            println!("{ms:.6},{sum}");
        }
    }
}
