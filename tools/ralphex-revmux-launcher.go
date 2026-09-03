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
	"syscall"
	"unsafe"
)

const (
	jobObjectExtendedLimitInformation = 9
	jobObjectLimitKillOnJobClose      = 0x00002000
)

var (
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	createJobObjectW         = kernel32.NewProc("CreateJobObjectW")
	setInformationJobObject  = kernel32.NewProc("SetInformationJobObject")
	assignProcessToJobObject = kernel32.NewProc("AssignProcessToJobObject")
	closeHandle              = kernel32.NewProc("CloseHandle")
)

type jobObjectBasicLimitInformation struct {
	perProcessUserTimeLimit int64
	perJobUserTimeLimit     int64
	limitFlags              uint32
	minimumWorkingSetSize   uintptr
	maximumWorkingSetSize   uintptr
	activeProcessLimit      uint32
	affinity                uintptr
	priorityClass           uint32
	schedulingClass         uint32
}

type ioCounters struct {
	readOperationCount  uint64
	writeOperationCount uint64
	otherOperationCount uint64
	readTransferCount   uint64
	writeTransferCount  uint64
	otherTransferCount  uint64
}

type jobObjectExtendedLimitInfo struct {
	basicLimitInformation jobObjectBasicLimitInformation
	ioInfo                ioCounters
	processMemoryLimit    uintptr
	jobMemoryLimit        uintptr
	peakProcessMemoryUsed uintptr
	peakJobMemoryUsed     uintptr
}

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
	if err := runInKillOnCloseJob(cmd); err != nil {
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

	if git, err := exec.LookPath("git.exe"); err == nil {
		root := filepath.Dir(filepath.Dir(git))
		if found := firstFile(gitBashCandidates(root)); found != "" {
			return found, nil
		}
	}

	var candidates []string
	roots := []string{
		os.Getenv("ProgramW6432"),
		os.Getenv("ProgramFiles"),
		os.Getenv("ProgramFiles(x86)"),
	}
	if localAppData := os.Getenv("LocalAppData"); localAppData != "" {
		roots = append(roots, filepath.Join(localAppData, "Programs"))
	}
	for _, root := range roots {
		if root == "" {
			continue
		}
		candidates = append(candidates, gitBashCandidates(filepath.Join(root, "Git"))...)
	}
	if found := firstFile(candidates); found != "" {
		return found, nil
	}

	for _, name := range []string{"bash.exe", "bash"} {
		if found, err := exec.LookPath(name); err == nil && isGitForWindowsBash(found) {
			return found, nil
		}
	}

	return "", fmt.Errorf("Git Bash not found; install Git for Windows or set RALPHEX_GIT_BASH")
}

func gitBashCandidates(root string) []string {
	return []string{
		filepath.Join(root, "bin", "bash.exe"),
		filepath.Join(root, "usr", "bin", "bash.exe"),
	}
}

func firstFile(candidates []string) string {
	for _, candidate := range candidates {
		if isFile(candidate) {
			return candidate
		}
	}
	return ""
}

func isGitForWindowsBash(path string) bool {
	path = strings.ToLower(filepath.ToSlash(filepath.Clean(path)))
	return strings.HasSuffix(path, "/git/bin/bash.exe") ||
		strings.HasSuffix(path, "/git/usr/bin/bash.exe") ||
		strings.HasSuffix(path, "/portablegit/bin/bash.exe") ||
		strings.HasSuffix(path, "/portablegit/usr/bin/bash.exe")
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

func runInKillOnCloseJob(cmd *exec.Cmd) error {
	job, err := newKillOnCloseJob()
	if err != nil {
		return fmt.Errorf("create child-process job: %w", err)
	}
	defer closeWindowsHandle(job)

	if err := cmd.Start(); err != nil {
		return err
	}
	var assignErr error
	if err := cmd.Process.WithHandle(func(processHandle uintptr) {
		assignErr = addProcessToJob(job, processHandle)
	}); err != nil {
		assignErr = err
	}
	if assignErr != nil {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return fmt.Errorf("assign Git Bash to child-process job: %w", assignErr)
	}
	return cmd.Wait()
}

func newKillOnCloseJob() (uintptr, error) {
	job, _, callErr := createJobObjectW.Call(0, 0)
	if job == 0 {
		return 0, windowsCallError("CreateJobObjectW", callErr)
	}

	info := jobObjectExtendedLimitInfo{}
	info.basicLimitInformation.limitFlags = jobObjectLimitKillOnJobClose
	ok, _, callErr := setInformationJobObject.Call(
		job,
		jobObjectExtendedLimitInformation,
		uintptr(unsafe.Pointer(&info)),
		unsafe.Sizeof(info),
	)
	if ok == 0 {
		closeWindowsHandle(job)
		return 0, windowsCallError("SetInformationJobObject", callErr)
	}
	return job, nil
}

func addProcessToJob(job, process uintptr) error {
	ok, _, callErr := assignProcessToJobObject.Call(job, process)
	if ok == 0 {
		return windowsCallError("AssignProcessToJobObject", callErr)
	}
	return nil
}

func closeWindowsHandle(handle uintptr) {
	if handle != 0 {
		_, _, _ = closeHandle.Call(handle)
	}
}

func windowsCallError(name string, callErr error) error {
	if callErr == nil || callErr == syscall.Errno(0) {
		return fmt.Errorf("%s failed", name)
	}
	return fmt.Errorf("%s: %w", name, callErr)
}

func fatalf(code int, format string, args ...any) {
	fmt.Fprintf(os.Stderr, "ralphex-revmux: "+format+"\n", args...)
	os.Exit(code)
}
