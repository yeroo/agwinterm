// ralphex-revmux-launcher is the native Windows entry point required by
// Ralphex's custom executor. Ralphex calls it with a rendered prompt path as
// the only argument; the launcher locates Git Bash and executes that prompt.
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

func main() {
	if len(os.Args) != 2 {
		fatalf(2, "expected one rendered prompt path")
	}
	if info, err := os.Stat(os.Args[1]); err != nil || info.IsDir() {
		fatalf(2, "prompt is not a readable file: %s", os.Args[1])
	}

	bash, err := findGitBash()
	if err != nil {
		fatalf(127, "%v", err)
	}

	cmd := exec.Command(bash, os.Args[1]) //nolint:gosec // the prompt path is Ralphex's explicit input
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	// These MSYS knobs are useful when launching Windows programs from an
	// interactive Git Bash, but inheriting them into the review shell prevents
	// native jq.exe from receiving converted /tmp paths.
	cmd.Env = withoutEnv(os.Environ(), "MSYS2_ARG_CONV_EXCL", "MSYS_NO_PATHCONV")
	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		fatalf(1, "could not launch Git Bash: %v", err)
	}
}

func findGitBash() (string, error) {
	if configured := os.Getenv("RALPHEX_GIT_BASH"); configured != "" {
		if isFile(configured) {
			return configured, nil
		}
		return "", fmt.Errorf("RALPHEX_GIT_BASH does not name a file: %s", configured)
	}

	for _, name := range []string{"bash.exe", "bash"} {
		if found, err := exec.LookPath(name); err == nil {
			return found, nil
		}
	}

	var candidates []string
	for _, root := range []string{
		os.Getenv("ProgramW6432"),
		os.Getenv("ProgramFiles"),
		os.Getenv("ProgramFiles(x86)"),
		filepath.Join(os.Getenv("LocalAppData"), "Programs"),
	} {
		if root == "" {
			continue
		}
		candidates = append(candidates,
			filepath.Join(root, "Git", "bin", "bash.exe"),
			filepath.Join(root, "Git", "usr", "bin", "bash.exe"),
		)
	}
	for _, candidate := range candidates {
		if isFile(candidate) {
			return candidate, nil
		}
	}

	return "", fmt.Errorf("Git Bash not found; install Git for Windows or set RALPHEX_GIT_BASH")
}

func isFile(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

func withoutEnv(environment []string, names ...string) []string {
	filtered := make([]string, 0, len(environment))
	for _, entry := range environment {
		name, _, _ := strings.Cut(entry, "=")
		remove := false
		for _, unwanted := range names {
			if strings.EqualFold(name, unwanted) {
				remove = true
				break
			}
		}
		if !remove {
			filtered = append(filtered, entry)
		}
	}
	return filtered
}

func fatalf(code int, format string, args ...any) {
	fmt.Fprintf(os.Stderr, "ralphex-revmux: "+format+"\n", args...)
	os.Exit(code)
}
