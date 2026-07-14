import { expect, test } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";
import { collectGeometry, waitForGeometrySettle } from "../lib/geometry";

// Layer 2: the REAL SpecDesk.Host.exe (Photino + WebView2), driven over CDP against a disposable git
// fixture repo. This proves the whole native startup path Layer 1's mock host can't: ready → auto-load
// of the fixture's welcome.md → lifecycle resolution from git → doc.loaded → real render. One app for
// the file (serial), built fresh (unless E2E_SKIP_BUILD=1) so a stale host can't pass.
test.describe.configure({ mode: "serial" });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

test("the real host boots, auto-loads welcome.md from the fixture repo, and renders both panes", async ({}, testInfo) => {
  const { page } = ctx;
  // The real Photino shell loaded.
  await expect(page).toHaveTitle("SpecDesk");
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "home");
  for (const edge of ["left", "right", "bottom"] as const) {
    await expect(page.locator("#" + edge + "-dock")).toHaveClass(/dock--collapsed/);
  }
  const titlebar = page.locator("#app-title");
  const minimize = page.getByRole("button", { name: "Minimize" });
  const maximize = page.getByRole("button", { name: "Maximize" });
  const close = page.getByRole("button", { name: "Close" });
  await expect(titlebar).toBeVisible();
  await expect(minimize).toBeVisible();
  await expect(maximize).toBeVisible();
  await expect(close).toBeVisible();

  // Drive the real WebView2 input path. The first mousedown enters Photino's native caption loop; only a
  // genuine second mousedown can prove that the titlebar still toggles the actual native window.
  await titlebar.dblclick();
  let restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  const maximizedGeometry = readNativeWindowGeometry(requireProcessId(ctx));
  expect(maximizedGeometry.window).toEqual(maximizedGeometry.workArea);
  await page.screenshot({
    path: testInfo.outputPath("chromeless-titlebar-maximized.png"),
    fullPage: true,
  });
  await titlebar.dblclick();
  await expect(maximize).toHaveAttribute("aria-pressed", "false");
  const restoredGeometry = readNativeWindowGeometry(requireProcessId(ctx));
  const verticalMiddle = Math.trunc((restoredGeometry.window.top + restoredGeometry.window.bottom) / 2);
  expect(
    nativeHitTest(requireProcessId(ctx), restoredGeometry.window.left + 1, verticalMiddle),
  ).toBe(10);
  expect(
    nativeHitTest(
      requireProcessId(ctx),
      restoredGeometry.window.right - 1,
      restoredGeometry.window.bottom - 1,
    ),
  ).toBe(17);

  // Keep the explicit button route covered independently from the titlebar gesture.
  await maximize.click();
  restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  await page.screenshot({ path: testInfo.outputPath("chromeless-window-maximized.png"), fullPage: true });
  await restore.click();
  await expect(maximize).toHaveAttribute("aria-pressed", "false");

  const sizeBefore = await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }));
  resizeNativeWindow(requireProcessId(ctx), 1120, 720);
  await expect
    .poll(() => page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight })))
    .not.toEqual(sizeBefore);
  const resized = await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }));
  expect(resized.width).toBeGreaterThan(900);
  expect(resized.height).toBeGreaterThan(600);

  // ready → the host auto-loaded the fixture's welcome.md → both real editors mounted from doc.loaded.
  await expect(page.locator("#editor .cm-editor")).toHaveCount(1);
  await expect(page.locator("#formatted .ProseMirror")).toHaveCount(1);
  // The formatted pane rendered the FIXTURE document, not the byte-identical-h1 bundled sample: assert
  // on text UNIQUE to the fixture's welcome.md, so a broken SPECDESK_DATA_ROOT redirect or a failed seed
  // short-circuit (either would load the bundled sample) fails here instead of passing green.
  await expect(page.locator("#formatted .ProseMirror h1")).toHaveText("Welcome to SpecDesk");
  await expect(page.locator("#formatted .ProseMirror")).toContainText("disposable fixture spec");

  const navigator = page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]');
  await navigator.click();
  await page.locator('#left-dock .dock-rail-btn[aria-label="Editor"]').click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "editor");

  // The lifecycle status surfaced a plain (non-empty, non-git) word — the git→lifecycle resolution ran.
  // Auto-retried so a status word that lands a beat after the render can't flake it.
  await expect(page.locator("#status")).not.toBeEmpty();

  // The full render + height-sync pipeline ran in the real WebView2: settle, then real spacers exist.
  await waitForGeometrySettle(page);
  const geometry = await collectGeometry(page);
  expect(geometry.spacers.length).toBeGreaterThan(0);

  // Evidence the agent reads — the real app's pixels.
  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});

test("the custom Close button completes the native close handshake", async () => {
  const close = ctx.page.getByRole("button", { name: "Close" });
  await expect(close).toBeVisible();
  await close.click();
  await expect.poll(() => ctx.app.process.exitCode).not.toBeNull();
});

function requireProcessId(fullApp: FullApp): number {
  const processId = fullApp.app.process.pid;
  if (processId === undefined) {
    throw new Error("The SpecDesk host process has no process id.");
  }
  return processId;
}

function resizeNativeWindow(processId: number, width: number, height: number): void {
  const script = `
& {
param([int]$TargetProcessId, [int]$Width, [int]$Height)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskNativeWindowTest {
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
}
"@
$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw "SpecDesk main window was not created." }
if (-not [SpecDeskNativeWindowTest]::MoveWindow($handle, 120, 100, $Width, $Height, $true)) {
  throw "MoveWindow failed."
}
} -TargetProcessId ${processId} -Width ${width} -Height ${height}
`;
  execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { stdio: "pipe" },
  );
}

interface NativeRect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

function readNativeWindowGeometry(processId: number): {
  window: NativeRect;
  workArea: NativeRect;
} {
  const script = `
& {
param([int]$TargetProcessId)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskNativeWindowGeometry {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO {
    public uint Size; public RECT Monitor; public RECT Work; public uint Flags;
  }
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
  [DllImport("user32.dll", EntryPoint="GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
"@
$handle = (Get-Process -Id $TargetProcessId).MainWindowHandle
$rect = [SpecDeskNativeWindowGeometry+RECT]::new()
if (-not [SpecDeskNativeWindowGeometry]::GetWindowRect($handle, [ref]$rect)) { throw "GetWindowRect failed." }
$info = [SpecDeskNativeWindowGeometry+MONITORINFO]::new()
$info.Size = [Runtime.InteropServices.Marshal]::SizeOf([type][SpecDeskNativeWindowGeometry+MONITORINFO])
$monitor = [SpecDeskNativeWindowGeometry]::MonitorFromWindow($handle, 2)
if (-not [SpecDeskNativeWindowGeometry]::GetMonitorInfo($monitor, [ref]$info)) { throw "GetMonitorInfo failed." }
Write-Output "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom),$($info.Work.Left),$($info.Work.Top),$($info.Work.Right),$($info.Work.Bottom)"
} -TargetProcessId ${processId}
`;
  const output = execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  ).trim();
  const values = output.split(",").map(Number);
  if (values.length !== 8 || values.some((value) => !Number.isFinite(value))) {
    throw new Error(`Unexpected native geometry: ${output}`);
  }
  const [left, top, right, bottom, workLeft, workTop, workRight, workBottom] = values as [
    number,
    number,
    number,
    number,
    number,
    number,
    number,
    number,
  ];
  return {
    window: { left, top, right, bottom },
    workArea: { left: workLeft, top: workTop, right: workRight, bottom: workBottom },
  };
}

function nativeHitTest(processId: number, x: number, y: number): number {
  const script = `
& {
param([int]$TargetProcessId, [int]$X, [int]$Y)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskNativeHitTest {
  [DllImport("user32.dll", EntryPoint="SendMessageW")]
  public static extern IntPtr SendMessage(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam);
}
"@
$handle = (Get-Process -Id $TargetProcessId).MainWindowHandle
$packed = (($Y -band 0xffff) -shl 16) -bor ($X -band 0xffff)
[SpecDeskNativeHitTest]::SendMessage($handle, 0x84, [UIntPtr]::Zero, [IntPtr]$packed).ToInt64()
} -TargetProcessId ${processId} -X ${x} -Y ${y}
`;
  const output = execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  ).trim();
  const result = Number(output);
  if (!Number.isFinite(result)) {
    throw new Error(`Unexpected native hit test: ${output}`);
  }
  return result;
}
