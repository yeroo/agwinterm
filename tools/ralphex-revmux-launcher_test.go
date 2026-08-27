package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"
)

func TestFindGitBashPrefersGitInstallOverPathBash(t *testing.T) {
	root := t.TempDir()
	gitBash := filepath.Join(root, "Git", "bin", "bash.exe")
	writeTestExecutable(t, gitBash)
	incompatible := filepath.Join(root, "incompatible", "bash.exe")
	writeTestExecutable(t, incompatible)

	t.Setenv("RALPHEX_GIT_BASH", "")
	t.Setenv("PATH", filepath.Dir(incompatible))
	t.Setenv("ProgramW6432", root)
	t.Setenv("ProgramFiles", filepath.Join(root, "unused-program-files"))
	t.Setenv("ProgramFiles(x86)", filepath.Join(root, "unused-program-files-x86"))
	t.Setenv("LocalAppData", filepath.Join(root, "unused-local-app-data"))

	got, err := findGitBash()
	if err != nil {
		t.Fatalf("findGitBash: %v", err)
	}
	if got != gitBash {
		t.Fatalf("findGitBash = %q, want Git-for-Windows candidate %q", got, gitBash)
	}
}

func TestFindGitBashRejectsUnrelatedPathBash(t *testing.T) {
	root := t.TempDir()
	incompatible := filepath.Join(root, "cygwin", "bash.exe")
	writeTestExecutable(t, incompatible)

	t.Setenv("RALPHEX_GIT_BASH", "")
	t.Setenv("PATH", filepath.Dir(incompatible))
	t.Setenv("ProgramW6432", filepath.Join(root, "unused-w6432"))
	t.Setenv("ProgramFiles", filepath.Join(root, "unused-program-files"))
	t.Setenv("ProgramFiles(x86)", filepath.Join(root, "unused-program-files-x86"))
	t.Setenv("LocalAppData", filepath.Join(root, "unused-local-app-data"))

	if got, err := findGitBash(); err == nil {
		t.Fatalf("findGitBash accepted unrelated PATH result %q", got)
	}
}

func TestKillOnCloseJobTerminatesAssignedProcess(t *testing.T) {
	job, err := newKillOnCloseJob()
	if err != nil {
		t.Fatalf("newKillOnCloseJob: %v", err)
	}

	cmd := exec.Command("powershell.exe", "-NoProfile", "-Command", "Start-Sleep -Seconds 60")
	if err := cmd.Start(); err != nil {
		closeWindowsHandle(job)
		t.Fatalf("start child: %v", err)
	}
	defer func() { _ = cmd.Process.Kill() }()

	var assignErr error
	if err := cmd.Process.WithHandle(func(processHandle uintptr) {
		assignErr = addProcessToJob(job, processHandle)
	}); err != nil {
		closeWindowsHandle(job)
		t.Fatalf("open child handle: %v", err)
	}
	if assignErr != nil {
		closeWindowsHandle(job)
		t.Fatalf("assign child to job: %v", assignErr)
	}

	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		closeWindowsHandle(job)
		t.Fatalf("child exited before job closure: %v", err)
	case <-time.After(200 * time.Millisecond):
	}

	closeWindowsHandle(job)
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("child survived closing a kill-on-close job")
	}
}

func writeTestExecutable(t *testing.T, path string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("create test executable directory: %v", err)
	}
	if err := os.WriteFile(path, []byte("fixture"), 0o755); err != nil {
		t.Fatalf("write test executable: %v", err)
	}
}
