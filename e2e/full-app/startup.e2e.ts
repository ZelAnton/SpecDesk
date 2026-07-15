import { expect, test } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
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
  expect(maximizedGeometry.client).toEqual(maximizedGeometry.window);
  const maximizedPointer = probeMaximizedWindowEdge(requireProcessId(ctx));
  expect(maximizedPointer.targetIsRoot).toBe(true);
  expect(maximizedPointer.hit).toBe(1);
  expect(maximizedPointer.arrowCursor).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath("chromeless-titlebar-maximized.png"),
    fullPage: true,
  });
  captureNativeWindow(
    requireProcessId(ctx),
    testInfo.outputPath("chromeless-native-maximized.png"),
  );
  await titlebar.dblclick();
  await expect(maximize).toHaveAttribute("aria-pressed", "false");
  const viewportBeforeResize = await page.evaluate(() => ({
    width: window.innerWidth,
    height: window.innerHeight,
  }));
  const liveResize = resizeWithRealPointerInput(requireProcessId(ctx));
  writeFileSync(
    testInfo.outputPath("native-resize-evidence.json"),
    JSON.stringify(liveResize, null, 2),
  );
  expect(liveResize.thickFrame).toBe(true);
  expect(liveResize.hasSystemCaption).toBe(false);
  expect(liveResize.left.targetClass).toBe("Photino");
  expect(liveResize.left.targetIsRoot).toBe(true);
  expect(liveResize.left.hit).toBe(10);
  expect(liveResize.left.cursorMatches).toBe(true);
  expect(liveResize.bottomRight.targetClass).toBe("Photino");
  expect(liveResize.bottomRight.targetIsRoot).toBe(true);
  expect(liveResize.bottomRight.hit).toBe(17);
  expect(liveResize.bottomRight.cursorMatches).toBe(true);
  expect(liveResize.before.left - liveResize.afterLeft.left).toBeGreaterThanOrEqual(100);
  expect(Math.abs(liveResize.afterLeft.right - liveResize.before.right)).toBeLessThanOrEqual(1);
  expect(liveResize.afterLeft.top).toBe(liveResize.before.top);
  expect(liveResize.afterLeft.bottom).toBe(liveResize.before.bottom);
  expect(liveResize.after.left).toBe(liveResize.afterLeft.left);
  expect(liveResize.after.top).toBe(liveResize.afterLeft.top);
  expect(liveResize.afterLeft.right - liveResize.after.right).toBeGreaterThanOrEqual(60);
  expect(liveResize.afterLeft.bottom - liveResize.after.bottom).toBeGreaterThanOrEqual(40);
  await expect
    .poll(() => page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight })))
    .not.toEqual(viewportBeforeResize);
  await page.screenshot({ path: testInfo.outputPath("chromeless-window-resized.png"), fullPage: true });

  // Keep the explicit button route covered independently from the titlebar gesture.
  await maximize.click();
  restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  await page.screenshot({ path: testInfo.outputPath("chromeless-window-maximized.png"), fullPage: true });
  await restore.click();
  await expect(maximize).toHaveAttribute("aria-pressed", "false");

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
  await page.locator('#left-dock .dock-rail-btn[aria-label="Outline"]').click();
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

interface NativeResizeProbe {
  targetClass: string;
  targetIsRoot: boolean;
  hit: number;
  cursorMatches: boolean;
}

interface NativeResizeEvidence {
  thickFrame: boolean;
  hasSystemCaption: boolean;
  left: NativeResizeProbe;
  bottomRight: NativeResizeProbe;
  before: NativeRect;
  afterLeft: NativeRect;
  after: NativeRect;
}

function resizeWithRealPointerInput(processId: number): NativeResizeEvidence {
  const script = `
& {
param([int]$TargetProcessId)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SpecDeskNativeResizeTest {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO {
    public int Size, Flags; public IntPtr Cursor; public POINT Point;
  }
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr handle, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int GetClassName(IntPtr handle, StringBuilder value, int length);
  [DllImport("user32.dll", EntryPoint="SendMessageW")]
  public static extern IntPtr SendMessage(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
  public static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetForegroundWindow(IntPtr handle);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetCursorInfo(ref CURSORINFO info);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
    public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo;
  }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
    [FieldOffset(0)] public MOUSEINPUT Mouse;
  }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT {
    public uint Type; public INPUTUNION Union;
  }
  [DllImport("user32.dll", SetLastError=true)]
  public static extern uint SendInput(uint count, INPUT[] inputs, int size);
  public static bool SendMouse(uint flags) {
    var input = new INPUT { Type = 0 };
    input.Union.Mouse.Flags = flags;
    return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
  }
}
"@
$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw "SpecDesk main window was not created." }
$originalCursor = [SpecDeskNativeResizeTest+POINT]::new()
if (-not [SpecDeskNativeResizeTest]::GetCursorPos([ref]$originalCursor)) {
  throw "GetCursorPos failed."
}
try {
if (-not [SpecDeskNativeResizeTest]::SetForegroundWindow($handle)) {
  throw "SpecDesk could not be brought to the foreground."
}
$before = [SpecDeskNativeResizeTest+RECT]::new()
if (-not [SpecDeskNativeResizeTest]::GetWindowRect($handle, [ref]$before)) {
  throw "GetWindowRect failed."
}

function Probe([int]$x, [int]$y, [int]$expectedCursor) {
  if (-not [SpecDeskNativeResizeTest]::SetCursorPos($x, $y)) { throw "SetCursorPos failed." }
  Start-Sleep -Milliseconds 250
  $point = [SpecDeskNativeResizeTest+POINT]::new()
  $point.X = $x; $point.Y = $y
  $target = [SpecDeskNativeResizeTest]::WindowFromPoint($point)
  $className = [Text.StringBuilder]::new(64)
  if ([SpecDeskNativeResizeTest]::GetClassName($target, $className, 64) -eq 0) {
    throw "GetClassName failed."
  }
  $packed = (($y -band 0xffff) -shl 16) -bor ($x -band 0xffff)
  $hit = [SpecDeskNativeResizeTest]::SendMessage(
    $target, 0x84, [UIntPtr]::Zero, [IntPtr]$packed).ToInt64()
  $cursor = [SpecDeskNativeResizeTest+CURSORINFO]::new()
  $cursor.Size = [Runtime.InteropServices.Marshal]::SizeOf(
    [type][SpecDeskNativeResizeTest+CURSORINFO])
  if (-not [SpecDeskNativeResizeTest]::GetCursorInfo([ref]$cursor)) {
    throw "GetCursorInfo failed."
  }
  $expected = [SpecDeskNativeResizeTest]::LoadCursor([IntPtr]::Zero, [IntPtr]$expectedCursor)
  if ($expected -eq [IntPtr]::Zero) { throw "LoadCursor failed." }
  [pscustomobject]@{
    targetClass = $className.ToString()
    targetIsRoot = [SpecDeskNativeResizeTest]::GetAncestor($target, 2) -eq $handle
    hit = $hit
    cursorMatches = (($cursor.Flags -band 1) -ne 0) -and $cursor.Cursor -eq $expected
  }
}

$leftX = $before.Left + 1
$middleY = [int](($before.Top + $before.Bottom) / 2)
$left = Probe $leftX $middleY 32644
if (-not $left.targetIsRoot -or $left.hit -ne 10 -or -not $left.cursorMatches) {
  throw "The left resize edge was not owned by SpecDesk."
}

  if (-not [SpecDeskNativeResizeTest]::SetCursorPos($leftX, $middleY)) { throw "SetCursorPos failed." }
  Start-Sleep -Milliseconds 250
  $dragPoint = [SpecDeskNativeResizeTest+POINT]::new()
  $dragPoint.X = $leftX; $dragPoint.Y = $middleY
  if ([SpecDeskNativeResizeTest]::GetAncestor(
      [SpecDeskNativeResizeTest]::WindowFromPoint($dragPoint), 2) -ne $handle) {
    throw "SpecDesk lost the left resize edge before pointer input."
  }
  if (-not [SpecDeskNativeResizeTest]::SendMouse(0x0002)) { throw "LEFTDOWN SendInput failed." }
  Start-Sleep -Milliseconds 250
  if (-not [SpecDeskNativeResizeTest]::SetCursorPos($leftX - 120, $middleY)) {
    throw "Resize SetCursorPos failed."
  }
  Start-Sleep -Milliseconds 400
  if (-not [SpecDeskNativeResizeTest]::SendMouse(0x0004)) { throw "LEFTUP SendInput failed." }
  Start-Sleep -Milliseconds 500

  $afterLeft = [SpecDeskNativeResizeTest+RECT]::new()
  if (-not [SpecDeskNativeResizeTest]::GetWindowRect($handle, [ref]$afterLeft)) {
    throw "GetWindowRect after left resize failed."
  }

  $cornerX = $afterLeft.Right - 1
  $cornerY = $afterLeft.Bottom - 1
  $bottomRight = Probe $cornerX $cornerY 32642
  if (-not $bottomRight.targetIsRoot -or $bottomRight.hit -ne 17 -or -not $bottomRight.cursorMatches) {
    throw "The bottom-right resize corner was not owned by SpecDesk after the left resize."
  }
  if (-not [SpecDeskNativeResizeTest]::SendMouse(0x0002)) { throw "Corner LEFTDOWN failed." }
  Start-Sleep -Milliseconds 250
  if (-not [SpecDeskNativeResizeTest]::SetCursorPos($cornerX - 80, $cornerY - 60)) {
    throw "Corner resize SetCursorPos failed."
  }
  Start-Sleep -Milliseconds 400
  if (-not [SpecDeskNativeResizeTest]::SendMouse(0x0004)) { throw "Corner LEFTUP failed." }
  Start-Sleep -Milliseconds 500

  $after = [SpecDeskNativeResizeTest+RECT]::new()
  if (-not [SpecDeskNativeResizeTest]::GetWindowRect($handle, [ref]$after)) {
    throw "GetWindowRect after corner resize failed."
  }
} finally {
  [SpecDeskNativeResizeTest]::SendMouse(0x0004) | Out-Null
  [SpecDeskNativeResizeTest]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
}
$style = [SpecDeskNativeResizeTest]::GetWindowLongPtr($handle, -16).ToInt64()
[pscustomobject]@{
  thickFrame = ($style -band 0x00040000) -ne 0
  hasSystemCaption = ($style -band 0x00c00000) -ne 0
  left = $left
  bottomRight = $bottomRight
  before = [pscustomobject]@{ left=$before.Left; top=$before.Top; right=$before.Right; bottom=$before.Bottom }
  afterLeft = [pscustomobject]@{ left=$afterLeft.Left; top=$afterLeft.Top; right=$afterLeft.Right; bottom=$afterLeft.Bottom }
  after = [pscustomobject]@{ left=$after.Left; top=$after.Top; right=$after.Right; bottom=$after.Bottom }
} | ConvertTo-Json -Compress -Depth 4
} -TargetProcessId ${processId}
`;
  const output = execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  ).trim();
  return JSON.parse(output) as NativeResizeEvidence;
}

interface NativeRect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

function captureNativeWindow(processId: number, outputPath: string): void {
  const escapedOutputPath = outputPath.replaceAll("'", "''");
  const script = `
& {
param([int]$TargetProcessId, [string]$OutputPath)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskNativeWindowCapture {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
}
"@
$handle = (Get-Process -Id $TargetProcessId).MainWindowHandle
$rect = [SpecDeskNativeWindowCapture+RECT]::new()
if (-not [SpecDeskNativeWindowCapture]::GetWindowRect($handle, [ref]$rect)) {
  throw "GetWindowRect failed."
}
$bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
try {
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
  } finally {
    $graphics.Dispose()
  }
} finally {
  $bitmap.Dispose()
}
} -TargetProcessId ${processId} -OutputPath '${escapedOutputPath}'
`;
  execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
    stdio: ["ignore", "pipe", "pipe"],
  });
}

interface MaximizedPointerEvidence {
  targetIsRoot: boolean;
  hit: number;
  arrowCursor: boolean;
}

function probeMaximizedWindowEdge(processId: number): MaximizedPointerEvidence {
  const script = `
& {
param([int]$TargetProcessId)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskMaximizedPointerTest {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO {
    public int Size, Flags; public IntPtr Cursor; public POINT Point;
  }
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr handle, uint flags);
  [DllImport("user32.dll", EntryPoint="SendMessageW")]
  public static extern IntPtr SendMessage(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetForegroundWindow(IntPtr handle);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetCursorInfo(ref CURSORINFO info);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
}
"@
$handle = (Get-Process -Id $TargetProcessId).MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw "SpecDesk main window was not created." }
$original = [SpecDeskMaximizedPointerTest+POINT]::new()
if (-not [SpecDeskMaximizedPointerTest]::GetCursorPos([ref]$original)) {
  throw "GetCursorPos failed."
}
try {
  if (-not [SpecDeskMaximizedPointerTest]::SetForegroundWindow($handle)) {
    throw "SpecDesk could not be brought to the foreground."
  }
  $rect = [SpecDeskMaximizedPointerTest+RECT]::new()
  if (-not [SpecDeskMaximizedPointerTest]::GetWindowRect($handle, [ref]$rect)) {
    throw "GetWindowRect failed."
  }
  $x = $rect.Left + 1
  $y = [int](($rect.Top + $rect.Bottom) / 2)
  if (-not [SpecDeskMaximizedPointerTest]::SetCursorPos($x, $y)) {
    throw "SetCursorPos failed."
  }
  Start-Sleep -Milliseconds 250
  $point = [SpecDeskMaximizedPointerTest+POINT]::new()
  $point.X = $x; $point.Y = $y
  $target = [SpecDeskMaximizedPointerTest]::WindowFromPoint($point)
  $packed = (($y -band 0xffff) -shl 16) -bor ($x -band 0xffff)
  $hit = [SpecDeskMaximizedPointerTest]::SendMessage(
    $target, 0x84, [UIntPtr]::Zero, [IntPtr]$packed).ToInt64()
  $cursor = [SpecDeskMaximizedPointerTest+CURSORINFO]::new()
  $cursor.Size = [Runtime.InteropServices.Marshal]::SizeOf(
    [type][SpecDeskMaximizedPointerTest+CURSORINFO])
  if (-not [SpecDeskMaximizedPointerTest]::GetCursorInfo([ref]$cursor)) {
    throw "GetCursorInfo failed."
  }
  $arrow = [SpecDeskMaximizedPointerTest]::LoadCursor([IntPtr]::Zero, [IntPtr]32512)
  [pscustomobject]@{
    targetIsRoot = [SpecDeskMaximizedPointerTest]::GetAncestor($target, 2) -eq $handle
    hit = $hit
    arrowCursor = (($cursor.Flags -band 1) -ne 0) -and $cursor.Cursor -eq $arrow
  } | ConvertTo-Json -Compress
} finally {
  [SpecDeskMaximizedPointerTest]::SetCursorPos($original.X, $original.Y) | Out-Null
}
} -TargetProcessId ${processId}
`;
  const output = execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  ).trim();
  return JSON.parse(output) as MaximizedPointerEvidence;
}

function readNativeWindowGeometry(processId: number): {
  window: NativeRect;
  client: NativeRect;
  workArea: NativeRect;
} {
  const script = `
& {
param([int]$TargetProcessId)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpecDeskNativeWindowGeometry {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO {
    public uint Size; public RECT Monitor; public RECT Work; public uint Flags;
  }
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetClientRect(IntPtr handle, out RECT rect);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool ClientToScreen(IntPtr handle, ref POINT point);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
  [DllImport("user32.dll", EntryPoint="GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
"@
$handle = (Get-Process -Id $TargetProcessId).MainWindowHandle
$rect = [SpecDeskNativeWindowGeometry+RECT]::new()
if (-not [SpecDeskNativeWindowGeometry]::GetWindowRect($handle, [ref]$rect)) { throw "GetWindowRect failed." }
$client = [SpecDeskNativeWindowGeometry+RECT]::new()
if (-not [SpecDeskNativeWindowGeometry]::GetClientRect($handle, [ref]$client)) { throw "GetClientRect failed." }
$clientTopLeft = [SpecDeskNativeWindowGeometry+POINT]::new()
$clientBottomRight = [SpecDeskNativeWindowGeometry+POINT]::new()
$clientBottomRight.X = $client.Right
$clientBottomRight.Y = $client.Bottom
if (-not [SpecDeskNativeWindowGeometry]::ClientToScreen($handle, [ref]$clientTopLeft)) { throw "ClientToScreen failed for top-left." }
if (-not [SpecDeskNativeWindowGeometry]::ClientToScreen($handle, [ref]$clientBottomRight)) { throw "ClientToScreen failed for bottom-right." }
$info = [SpecDeskNativeWindowGeometry+MONITORINFO]::new()
$info.Size = [Runtime.InteropServices.Marshal]::SizeOf([type][SpecDeskNativeWindowGeometry+MONITORINFO])
$monitor = [SpecDeskNativeWindowGeometry]::MonitorFromWindow($handle, 2)
if (-not [SpecDeskNativeWindowGeometry]::GetMonitorInfo($monitor, [ref]$info)) { throw "GetMonitorInfo failed." }
Write-Output "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom),$($clientTopLeft.X),$($clientTopLeft.Y),$($clientBottomRight.X),$($clientBottomRight.Y),$($info.Work.Left),$($info.Work.Top),$($info.Work.Right),$($info.Work.Bottom)"
} -TargetProcessId ${processId}
`;
  const output = execFileSync(
    "powershell.exe",
    ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  ).trim();
  const values = output.split(",").map(Number);
  if (values.length !== 12 || values.some((value) => !Number.isFinite(value))) {
    throw new Error(`Unexpected native geometry: ${output}`);
  }
  const [
    left,
    top,
    right,
    bottom,
    clientLeft,
    clientTop,
    clientRight,
    clientBottom,
    workLeft,
    workTop,
    workRight,
    workBottom,
  ] = values as [
    number,
    number,
    number,
    number,
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
    client: { left: clientLeft, top: clientTop, right: clientRight, bottom: clientBottom },
    workArea: { left: workLeft, top: workTop, right: workRight, bottom: workBottom },
  };
}
