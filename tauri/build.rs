use std::env;
use std::fs;
use std::path::{Path, PathBuf};

fn main() {
    sync_compare_resources();
    tauri_build::build();
}

fn sync_compare_resources() {
    let manifest_dir =
        PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR not set"));
    let source_dir = manifest_dir.join("..").join("backend").join("Resources");
    let targets = [
        (
            source_dir.join("libreoffice-compare.py"),
            manifest_dir
                .join("resources")
                .join("libreoffice-compare.py"),
        ),
        (
            source_dir.join("libreoffice-compare.py"),
            manifest_dir
                .join("resources")
                .join("binaries")
                .join("libreoffice-compare.py"),
        ),
        (
            source_dir.join("word-compare.scpt"),
            manifest_dir.join("resources").join("word-compare.scpt"),
        ),
        (
            source_dir.join("word-compare.scpt"),
            manifest_dir
                .join("resources")
                .join("binaries")
                .join("word-compare.scpt"),
        ),
    ];

    println!(
        "cargo:rerun-if-changed={}",
        source_dir.join("libreoffice-compare.py").display()
    );
    println!(
        "cargo:rerun-if-changed={}",
        source_dir.join("word-compare.scpt").display()
    );

    for (source, target) in targets {
        copy_file(&source, &target);
    }
}

fn copy_file(source: &Path, target: &Path) {
    if !source.exists() {
        panic!("Missing resource source file: {}", source.display());
    }

    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent)
            .unwrap_or_else(|err| panic!("Failed to create {}: {err}", parent.display()));
    }

    fs::copy(source, target).unwrap_or_else(|err| {
        panic!(
            "Failed to copy {} to {}: {err}",
            source.display(),
            target.display()
        )
    });
}
