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
	createSuspended                   = 0x00000004
	threadSuspendResume               = 0x0002
	th32csSnapThread                  = 0x00000004
	jobObjectExtendedLimitInformation = 9
	jobObjectLimitKillOnJobClose      = 0x00002000
	invalidHandleValue                = ^uintptr(0)
)

var (
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	createJobObjectW         = kernel32.NewProc("CreateJobObjectW")
	setInformationJobObject  = kernel32.NewProc("SetInformationJobObject")
	assignProcessToJobObject = kernel32.NewProc("AssignProcessToJobObject")
	createToolhelp32Snapshot = kernel32.NewProc("CreateToolhelp32Snapshot")
	thread32First            = kernel32.NewProc("Thread32First")
	thread32Next             = kernel32.NewProc("Thread32Next")
	openThread               = kernel32.NewProc("OpenThread")
	resumeThread             = kernel32.NewProc("ResumeThread")
	isProcessInJob           = kernel32.NewProc("IsProcessInJob")
	closeHandle              = kernel32.NewProc("CloseHandle")
	// Test synchronization seam: production leaves this nil. A test can stop the child at the exact
	// point after CreateProcess returns and before assignment, proving CREATE_SUSPENDED is effective.
	afterSuspendedStart func(*exec.Cmd) error
)

type threadEntry32 struct {
	size           uint32
	usage          uint32
	threadID       uint32
	ownerProcessID uint32
	basePriority   int32
	deltaPriority  int32
	flags          uint32
}

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

	if err := startProcessInJob(cmd, job, resumeProcessThreads); err != nil {
		return err
	}
	return cmd.Wait()
}

// startProcessInJob closes the process-creation race in which a fast child can
// spawn descendants before AssignProcessToJobObject runs. The primary thread is
// created suspended, assigned to the job, and resumed only after assignment.
// resume is injected so assignment can be asserted at the resume boundary without racing the child.
func startProcessInJob(cmd *exec.Cmd, job uintptr, resume func(int) error) error {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.CreationFlags |= createSuspended
	if err := cmd.Start(); err != nil {
		return err
	}

	failStarted := func(err error) error {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return err
	}
	if afterSuspendedStart != nil {
		if err := afterSuspendedStart(cmd); err != nil {
			return failStarted(fmt.Errorf("after suspended process start: %w", err))
		}
	}
	var assignErr error
	if err := cmd.Process.WithHandle(func(processHandle uintptr) {
		assignErr = addProcessToJob(job, processHandle)
	}); err != nil {
		assignErr = err
	}
	if assignErr != nil {
		return failStarted(fmt.Errorf("assign Git Bash to child-process job: %w", assignErr))
	}
	if err := resume(cmd.Process.Pid); err != nil {
		return failStarted(fmt.Errorf("resume Git Bash after job assignment: %w", err))
	}
	return nil
}

func resumeProcessThreads(pid int) error {
	snapshot, _, callErr := createToolhelp32Snapshot.Call(th32csSnapThread, 0)
	if snapshot == invalidHandleValue {
		return windowsCallError("CreateToolhelp32Snapshot", callErr)
	}
	defer closeWindowsHandle(snapshot)

	entry := threadEntry32{size: uint32(unsafe.Sizeof(threadEntry32{}))}
	ok, _, callErr := thread32First.Call(snapshot, uintptr(unsafe.Pointer(&entry)))
	if ok == 0 {
		return windowsCallError("Thread32First", callErr)
	}
	resumed := false
	for {
		if entry.ownerProcessID == uint32(pid) {
			thread, _, openErr := openThread.Call(threadSuspendResume, 0, uintptr(entry.threadID))
			if thread == 0 {
				return windowsCallError("OpenThread", openErr)
			}
			previous, _, resumeErr := resumeThread.Call(thread)
			closeWindowsHandle(thread)
			if previous == uintptr(^uint32(0)) {
				return windowsCallError("ResumeThread", resumeErr)
			}
			resumed = true
		}

		entry.size = uint32(unsafe.Sizeof(entry))
		next, _, nextErr := thread32Next.Call(snapshot, uintptr(unsafe.Pointer(&entry)))
		if next != 0 {
			continue
		}
		if errno, ok := nextErr.(syscall.Errno); ok && errno == syscall.ERROR_NO_MORE_FILES {
			break
		}
		return windowsCallError("Thread32Next", nextErr)
	}
	if !resumed {
		return fmt.Errorf("no thread found for process %d", pid)
	}
	return nil
}

func processInJob(process, job uintptr) (bool, error) {
	var assigned uint32
	ok, _, callErr := isProcessInJob.Call(process, job, uintptr(unsafe.Pointer(&assigned)))
	if ok == 0 {
		return false, windowsCallError("IsProcessInJob", callErr)
	}
	return assigned != 0, nil
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
