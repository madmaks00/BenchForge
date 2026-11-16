fn main() {
    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".into());
    let output = std::process::Command::new(rustc)
        .arg("--version")
        .output()
        .expect("rustc version");
    println!(
        "cargo:rustc-env=BENCHFORGE_RUSTC={}",
        String::from_utf8(output.stdout)
            .expect("version UTF8")
            .trim()
    );
}
