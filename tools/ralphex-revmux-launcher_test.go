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

func TestRunInKillOnCloseJobSuspendsTheProductionChildBeforeAssignment(t *testing.T) {
	marker := filepath.Join(t.TempDir(), "started")
	cmd := exec.Command(os.Args[0], "-test.run=^TestLauncherHelperProcess$")
	cmd.Env = append(os.Environ(), "AGW_LAUNCH_HELPER=1", "AGW_LAUNCH_MARKER="+marker)

	checkpoint := make(chan *exec.Cmd, 1)
	release := make(chan struct{})
	released := false
	previousCheckpoint := afterSuspendedStart
	afterSuspendedStart = func(started *exec.Cmd) error {
		checkpoint <- started
		<-release
		return nil
	}
	defer func() {
		if !released {
			close(release)
		}
		afterSuspendedStart = previousCheckpoint
	}()

	done := make(chan error, 1)
	go func() { done <- runInKillOnCloseJob(cmd) }()

	var started *exec.Cmd
	select {
	case started = <-checkpoint:
	case err := <-done:
		t.Fatalf("runInKillOnCloseJob exited before the post-start checkpoint: %v", err)
	case <-time.After(5 * time.Second):
		t.Fatal("runInKillOnCloseJob did not reach the post-start checkpoint")
	}
	if started.SysProcAttr == nil || started.SysProcAttr.CreationFlags&createSuspended == 0 {
		t.Fatal("production child was not created with CREATE_SUSPENDED")
	}

	// The helper is the already-built test executable and writes immediately on entry. Holding the
	// launcher here makes this a deterministic observation before AssignProcessToJobObject, rather
	// than a race against a comparatively slow PowerShell startup.
	time.Sleep(250 * time.Millisecond)
	if _, err := os.Stat(marker); !os.IsNotExist(err) {
		t.Fatalf("child executed before job assignment: marker err=%v", err)
	}

	close(release)
	released = true
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("runInKillOnCloseJob: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("production child did not exit after assignment and resume")
	}
	if _, err := os.Stat(marker); err != nil {
		t.Fatalf("child did not execute after assignment and resume: %v", err)
	}
}

func TestLauncherHelperProcess(t *testing.T) {
	if os.Getenv("AGW_LAUNCH_HELPER") != "1" {
		return
	}
	if err := os.WriteFile(os.Getenv("AGW_LAUNCH_MARKER"), []byte("started"), 0o600); err != nil {
		os.Exit(3)
	}
	os.Exit(0)
}

func TestProcessIsAssignedBeforeItCanExecute(t *testing.T) {
	job, err := newKillOnCloseJob()
	if err != nil {
		t.Fatalf("newKillOnCloseJob: %v", err)
	}

	marker := filepath.Join(t.TempDir(), "started")
	cmd := exec.Command("powershell.exe", "-NoProfile", "-Command",
		"[IO.File]::WriteAllText($env:AGW_LAUNCH_MARKER, 'started'); Start-Sleep -Seconds 60")
	cmd.Env = append(os.Environ(), "AGW_LAUNCH_MARKER="+marker)
	if err := startProcessInJob(cmd, job, func(pid int) error {
		if _, err := os.Stat(marker); !os.IsNotExist(err) {
			t.Fatalf("child executed before job assignment: marker err=%v", err)
		}
		var assigned bool
		var inspectErr error
		if err := cmd.Process.WithHandle(func(processHandle uintptr) {
			assigned, inspectErr = processInJob(processHandle, job)
		}); err != nil {
			return err
		}
		if inspectErr != nil {
			return inspectErr
		}
		if !assigned {
			t.Fatal("child was not assigned when its primary thread was resumed")
		}
		return resumeProcessThreads(pid)
	}); err != nil {
		closeWindowsHandle(job)
		t.Fatalf("start suspended child in job: %v", err)
	}
	defer func() { _ = cmd.Process.Kill() }()

	deadline := time.Now().Add(5 * time.Second)
	for {
		if _, err := os.Stat(marker); err == nil {
			break
		}
		if time.Now().After(deadline) {
			closeWindowsHandle(job)
			t.Fatal("child did not execute after its assigned thread was resumed")
		}
		time.Sleep(20 * time.Millisecond)
	}

	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()

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
