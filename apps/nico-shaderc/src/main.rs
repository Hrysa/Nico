use std::{
    env,
    error::Error,
    ffi::OsString,
    fs, io,
    path::{Path, PathBuf},
    process::Command,
};

use clap::Parser;

const SOURCE: &str = "assets/presentation/shaders/bootstrap.slang";
const OUTPUT: &str = "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl";

#[derive(Debug, Parser)]
#[command(
    name = "nico-shaderc",
    about = "Compiles Nico's Slang shaders into runtime RHI artifacts"
)]
struct Args {
    /// Verifies that generated artifacts are current without replacing them.
    #[arg(long)]
    check: bool,

    /// Slang compiler executable; defaults to NICO_SLANGC, then slangc on PATH.
    #[arg(long, value_name = "PATH")]
    slangc: Option<PathBuf>,

    /// Project root containing assets/presentation; defaults to searching from the current directory.
    #[arg(long, value_name = "PATH")]
    root: Option<PathBuf>,
}

fn main() -> Result<(), Box<dyn Error>> {
    let args = Args::parse();
    let project_root = find_project_root(args.root.as_deref())?;
    let source = project_root.join(SOURCE);
    let output = project_root.join(OUTPUT);
    let compiler = args
        .slangc
        .map(PathBuf::into_os_string)
        .or_else(|| env::var_os("NICO_SLANGC"))
        .unwrap_or_else(|| OsString::from("slangc"));

    if args.check {
        check_artifact(&compiler, &source, &output)?;
        println!("shader artifacts are current");
    } else {
        compile_shader(&compiler, &source, &output)?;
        println!("generated {}", output.display());
    }
    Ok(())
}

fn find_project_root(explicit: Option<&Path>) -> io::Result<PathBuf> {
    if let Some(root) = explicit {
        let root = root.canonicalize()?;
        if root.join(SOURCE).is_file() {
            return Ok(root);
        }
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("{} does not contain {SOURCE}", root.display()),
        ));
    }

    let current = env::current_dir()?;
    current
        .ancestors()
        .find(|candidate| candidate.join(SOURCE).is_file())
        .map(Path::to_path_buf)
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                format!(
                    "could not find {SOURCE} from {}; pass --root",
                    current.display()
                ),
            )
        })
}

fn check_artifact(compiler: &std::ffi::OsStr, source: &Path, output: &Path) -> io::Result<()> {
    let temporary = output.with_extension(format!("wgsl.{}.check", std::process::id()));
    let _temporary_guard = RemoveFileOnDrop(temporary.clone());
    compile_shader(compiler, source, &temporary)?;

    let expected = fs::read_to_string(output)?;
    let actual = fs::read_to_string(&temporary)?;
    if normalize_artifact(&expected) != normalize_artifact(&actual) {
        return Err(io::Error::other(format!(
            "{} is stale; run nico-shaderc to regenerate it",
            output.display()
        )));
    }
    Ok(())
}

fn compile_shader(compiler: &std::ffi::OsStr, source: &Path, output: &Path) -> io::Result<()> {
    let parent = output
        .parent()
        .ok_or_else(|| io::Error::other("shader output has no parent directory"))?;
    fs::create_dir_all(parent)?;

    let status = Command::new(compiler)
        .arg(source)
        .args([
            "-target",
            "wgsl",
            "-entry",
            "vertex_main",
            "-stage",
            "vertex",
            "-entry",
            "fragment_main",
            "-stage",
            "fragment",
            "-o",
        ])
        .arg(output)
        .status()?;

    if !status.success() {
        return Err(io::Error::other(format!(
            "Slang compilation failed with {status}"
        )));
    }

    let generated = fs::read_to_string(output)?;
    fs::write(output, normalize_artifact(&generated))?;
    Ok(())
}

fn normalize_artifact(value: &str) -> String {
    format!("{}\n", value.replace("\r\n", "\n").trim_end())
}

struct RemoveFileOnDrop(PathBuf);

impl Drop for RemoveFileOnDrop {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::{Args, find_project_root, normalize_artifact};
    use clap::Parser;

    #[test]
    fn check_mode_is_optional() {
        assert!(!Args::try_parse_from(["nico-shaderc"]).unwrap().check);
        assert!(
            Args::try_parse_from(["nico-shaderc", "--check"])
                .unwrap()
                .check
        );
    }

    #[test]
    fn artifact_comparison_normalizes_line_endings_and_trailing_space() {
        assert_eq!(
            normalize_artifact("a\r\nb\r\n"),
            normalize_artifact("a\nb\n")
        );
        assert_eq!(normalize_artifact("a\r\nb\r\n \r\n"), "a\nb\n");
    }

    #[test]
    fn explicit_project_root_must_contain_the_shader_source() {
        let root =
            find_project_root(Some(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))).unwrap_err();
        assert_eq!(root.kind(), std::io::ErrorKind::NotFound);

        let workspace = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
        assert!(find_project_root(Some(&workspace)).is_ok());
    }
}
