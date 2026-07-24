fn main() {
    let out = std::path::PathBuf::from("../../native/agwinterm-ptyhost/src");
    prost_build::Config::new()
        .out_dir(&out)
        .compile_protos(
            &["../../proto/ptyhost.proto", "../../proto/persist.proto"],
            &["../../proto"],
        )
        .expect("prost generation failed");
    std::fs::rename(out.join("agwinterm.ptyhost.rs"), out.join("proto.rs")).expect("rename ptyhost");
    std::fs::rename(out.join("agwinterm.persist.rs"), out.join("persist.rs")).expect("rename persist");
    println!("wrote native/agwinterm-ptyhost/src/{{proto,persist}}.rs");
}
