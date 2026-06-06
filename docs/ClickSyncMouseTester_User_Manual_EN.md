# ClickSyncMouseTester User Manual

ClickSyncMouseTester is a Windows mouse-input testing tool. It observes what a normal Windows application receives through Raw Input, then presents that data as report-rate metrics, button timing, motion charts, sensitivity-matching results, and angle-calibration guidance.

This manual explains what each page is for, how to use it, what the main metrics mean, and how to interpret the results.

---

## 1. Report Rate Test

### 1.1 What This Page Does

The report-rate page measures how frequently the selected mouse reports input data to Windows.

Common advertised mouse report rates include:

- 125 Hz
- 500 Hz
- 1000 Hz
- 2000 Hz
- 4000 Hz
- 8000 Hz

A higher report rate usually means more frequent position updates and lower theoretical input latency. In practice, the result is affected by firmware, receiver placement, USB topology, Windows scheduling, drivers, and current system load.

This page measures:

> The mouse report rate actually observed at the Windows Raw Input layer.

It is not a USB bus analyzer. Treat the result as an application-level view of what your current PC and OS environment can actually receive.

---

### 1.2 When To Use It

Use this page to check:

- Whether a mouse reaches something close to its advertised report rate.
- Whether a 4000 Hz or 8000 Hz mouse is really reporting at high frequency.
- Whether wireless mode is stable.
- Whether USB ports, receiver placement, driver settings, or system load affect the result.
- Whether motion produces obvious drops, jitter, or zero-motion reports.

---

### 1.3 Basic Steps

1. Open the app and enter **Report Rate Test**.
2. Select the mouse device on the right side.
3. Choose a counting mode:
   - **Raw Packet**
   - **Motion Report**
4. Start the test.
5. Move the mouse quickly and continuously inside the capture area for **3-5 seconds**.
6. Watch the main number and the bottom metrics.
7. Press `Esc` or right-click to leave the locked capture state.

Recommended testing habits:

- Keep the mouse moving. Do not only nudge it slightly.
- Draw fast, continuous circles.
- Test for 3-5 seconds instead of making one short swipe.
- For high-report-rate mice, close background apps that heavily use CPU or GPU.

---

### 1.4 Counting Modes

#### Raw Packet

Raw Packet counts every Raw Input mouse packet from the selected device.

This can include:

- Motion packets.
- Zero-motion packets.
- Button-related packets.
- Wheel-related packets.

Use this mode when you want to know how many mouse reports Windows receives overall.

#### Motion Report

Motion Report only counts reports with movement:

```text
DeltaX != 0 or DeltaY != 0
```

It filters zero-motion packets and is closer to the effective position-update rate while the mouse is actually moving.

Use it to inspect:

- Effective report rate during motion.
- Zero-motion packet ratio.
- Stability of high-report-rate mice while moving.

---

### 1.5 Main Number: Raw Input Report Rate

The large central value is the current Raw Input report rate.

It is estimated from a short recent time window of about 300 ms and is displayed in Hz.

Example:

```text
1000 Hz
```

This means the app is currently receiving about 1000 mouse reports per second.

Notes:

- The value is a live estimate and will naturally move up and down.
- High-report-rate mice need continuous fast movement to measure well.
- Slow movement may underestimate high-report-rate devices.

---

### 1.6 Bottom Metrics

#### Peak Rate

Peak rate is the highest notable report rate reached during the current test.

It is useful for quickly checking whether the mouse ever approached its advertised value.

Examples:

- Advertised 1000 Hz, peak near 1000 Hz: usually normal.
- Advertised 8000 Hz, peak stuck near 1000 Hz: settings, USB, receiver placement, or system environment may be limiting the device.

#### 1 Second Interval Jitter

Interval jitter describes the stability of report spacing over the most recent 1 second.

It is a percentage:

```text
interval jitter = standard deviation of report intervals / average report interval * 100%
```

Simple interpretation:

- Lower is more stable.
- Higher means the reports are less evenly spaced.

This is not the report rate itself. It is a helper metric for report-rate stability.

To avoid unstable startup data, the app waits for about 1 second after the first valid packet before showing this value.

#### Zero Packet Ratio

The zero packet ratio is shown in **Motion Report** mode.

It is the ratio of Raw Input packets in the recent window where:

```text
DeltaX == 0 and DeltaY == 0
```

It tells you how many reports carried no actual movement during the test.

Notes:

- A high zero packet ratio does not always mean the mouse is broken.
- Interpret it together with movement speed, DPI, firmware behavior, and test method.
- The app starts this statistic after the first valid motion packet, so idle time before movement is not counted.

#### Dropped Packet Count

Dropped packet count is the number of packets discarded because the app's internal buffer overflowed.

It should normally remain at 0.

If it keeps increasing, common causes include:

- High system load.
- A very high report rate creating processing pressure.
- The current environment cannot consume all Raw Input packets steadily.

---

### 1.7 Pause And Resume

When paused, the app freezes the latest normal snapshot instead of recalculating from the pause moment.

This avoids:

- Abnormal packets caused by clicking stop or right-clicking out of capture.
- Long intervals at the stop moment polluting jitter results.
- Metrics jumping to strange values right after pausing.

After resuming, interval jitter and zero packet ratio wait for valid packets and complete their warm-up period again.

---

### 1.8 How To Read Results

Focus on three things:

1. **Whether the main value is close to the advertised report rate.**
2. **Whether 1 second interval jitter is unusually high.**
3. **Whether dropped packet count stays at 0.**

If the report rate is lower than expected, try:

- Checking the mouse driver or control software report-rate setting.
- Switching USB ports.
- Moving a wireless receiver closer to the mouse.
- Disabling power-saving modes.
- Reducing background load.
- Retesting with Motion Report mode.

---

### 1.9 How It Works

Windows exposes low-level input data to applications through Raw Input.

ClickSyncMouseTester listens to Raw Input packets from the selected mouse and records packet arrival timestamps.

From those timestamps, it calculates:

- Current report rate in a short window of about 300 ms.
- Recent 1 second interval stability.
- Recent zero-motion packet ratio.
- Whether the internal buffer dropped any packets.

The result represents:

> The selected mouse's observed input behavior in the current Windows user-mode environment.

---

## 2. Button Double-Click Test

### 2.1 What This Page Does

The button test page observes mouse button, wheel, and custom key input state and timing.

It can inspect:

- Left, middle, right, forward, and back button down/up events.
- Wheel up and wheel down counts.
- Down-to-down intervals.
- Down-to-up hold durations.
- Double-click counts inside a configurable threshold.
- A custom keyboard key or special key selected by the user.

Use this page to diagnose accidental double-clicks, switch bounce, unstable releases, wheel misfires, and similar input issues.

---

### 2.2 Basic Steps

1. Enter **Button Double-Click Test**.
2. Press or click the mouse button you want to test.
3. Watch the corresponding card for down count, up count, double-click count, and timing values.
4. Scroll the wheel and check up/down wheel counts.
5. Use **Custom Key Detection** if you need to test a keyboard key or another non-standard key.
6. Click **Reset Statistics** to clear all page statistics.

The page highlights a button while it is currently pressed. If the highlight remains after release, Windows did not receive the matching release event or the device/driver reported an abnormal state.

---

### 2.3 Mouse Double-Click Threshold

The **Mouse Double-Click Threshold** determines whether two down events count as a double-click.

Example:

```text
80 ms
```

If the same button is pressed twice within less than 80 ms, the page counts it as a double-click.

A common diagnostic range is **60-120 ms**:

- Lower values are stricter and better for catching very short bounce.
- Higher values are more likely to count rapid repeated clicks as double-clicks.

This threshold only affects this page's statistics. It does not change the Windows double-click speed setting or mouse firmware.

---

### 2.4 Metrics

#### Double Clicks

The double-click count increases when two down events on the same button occur inside the current threshold.

If it increases after what should have been a single click, the button may be bouncing or reporting duplicate down events.

#### Down Count / Up Count

Down count is the number of down events received.

Up count is the number of up events received.

During normal clicking, they should usually stay close. A mismatch can indicate duplicate down events, missing release events, or a device state issue.

#### Down -> Down

Down -> Down is the interval between the current down event and the previous down event.

The page shows current, minimum, and average values. Use this to inspect:

- Repeat-click rhythm.
- Very short repeated triggers.
- Your real manual double-click speed.

#### Down -> Up

Down -> Up is the duration of a single press.

It helps inspect:

- Click hold time.
- Whether long presses stay stable.
- Whether a press is released unexpectedly quickly.

---

### 2.5 Wheel Test

The wheel area counts:

- **Wheel Up**
- **Wheel Down**

Each received wheel report increments the matching direction and briefly highlights it.

If scrolling up produces down counts, or one scroll step produces many opposite-direction reports, the wheel encoder, wheel structure, or driver layer may be unstable.

---

### 2.6 Custom Key Detection

Custom Key Detection tests a key that is not part of the fixed mouse button cards.

Steps:

1. Click **Record**.
2. Press the key you want to test once.
3. After the selected key is shown, press and release it normally.
4. Watch down count, up count, double-click count, and timing metrics.
5. Click **Reset** to clear the current custom-key statistics.
6. Click **Record** again to select a different key.

Press `Esc` while recording to cancel.

The custom key has its own double-click threshold. It does not affect the fixed mouse-button cards.

---

### 2.7 How To Read Results

Focus on four checks:

1. **One physical click should normally add one down and one up.**
2. **Double-click count should not increase during normal single clicks.**
3. **The minimum Down -> Down value should not be suspiciously close to 0.**
4. **Down -> Up should match the actual time you held the button.**

If you suspect double-clicking:

- Set the double-click threshold to 60-80 ms.
- Click the same button repeatedly with normal force.
- Watch whether double-click count increases unexpectedly.
- Try different click force and click positions to see whether the issue is repeatable.

Rare intermittent issues may require dozens of clicks to reproduce.

---

### 2.8 How It Works

The page listens to Raw Input button, wheel, and keyboard events.

For every down event, it records a timestamp and compares it with the previous down timestamp.

For every up event, it subtracts the latest down timestamp from the up timestamp to estimate hold duration.

Double-click detection is based only on the time between two down events and the configured threshold.

---

## 3. Mouse Performance Analysis

### 3.1 What This Page Does

Mouse Performance Analysis captures Raw Input data from one selected mouse and opens a separate chart window for detailed motion, timing, and distribution analysis.

It is better than the report-rate page when you need a longer session or deeper inspection, such as:

- Inspecting every DeltaX / DeltaY sample in a movement.
- Looking for report-interval spikes.
- Comparing different mice or different settings.
- Exporting a capture and importing it later.
- Saving chart PNGs for notes or reports.

---

### 3.2 Basic Steps

1. Enter **Mouse Performance Analysis**.
2. Choose the target mouse in **Target Device**.
3. Enter the current mouse CPI / DPI in **Mouse DPI**.
4. Click **Start Capture** or press `S` to enter locked capture.
5. Move the mouse in the way you want to test.
6. Press `S` again, press `Esc`, right-click, or leave the window to end the current capture segment.
7. Click **Chart** to open the chart window.
8. Click **Resume Capture** or press `S` to append more data to the same session.
9. Click **Reset** to clear the current session.

Ending a capture segment pauses and preserves the session. Resuming appends to it. Only Reset clears it.

---

### 3.3 DPI / CPI Input

DPI / CPI is used to convert mouse counts into physical distance and speed.

Example:

```text
1600
```

The app then interprets movement as 1600 counts per inch.

Notes:

- The value must be greater than 0.
- Invalid input falls back to the last valid value.
- Report count, Delta values, and timing remain useful even if DPI is wrong.
- Speed and physical distance require a correct DPI value.
- The app does not change your device DPI.

---

### 3.4 Live Summary

#### Report Count

The number of Raw Input mouse reports in the current session.

More reports generally give charts and statistics more value. Very short captures may not be representative.

#### X Net Displacement / Y Net Displacement

The sum of DeltaX and DeltaY across the session.

Back-and-forth motion can make net displacement close to 0 even though the mouse moved a lot.

#### Motion Path

Motion path accumulates per-packet movement length.

It is closer to "how far the mouse actually traveled" because it is not canceled out by reverse motion.

#### Speed

Speed is estimated from Raw Input movement, timestamps, and the entered CPI. It is shown in m/s.

Use it to inspect fast flicks, slow micro-movement, and different DPI settings.

---

### 3.5 Data Quality

The data-quality line can include:

- **Reports**: captured report count.
- **Zero motion**: reports where DeltaX and DeltaY are both 0.
- **Control**: button, wheel, or control-only reports.
- **Dropped**: packets discarded because the internal queue overflowed.
- **Quality**: current quality state, such as good or degraded.
- **Queue**: high-watermark count and queue capacity.

Dropped packets should normally remain 0, and quality should ideally remain good.

If quality is degraded or queue pressure is high, try:

- Shortening the capture.
- Closing heavy background apps.
- Lowering the mouse report rate and retesting.
- Using a more stable USB port or wireless receiver position.

---

### 3.6 Chart Window

Click **Chart** to open the separate chart window.

The chart window supports:

- Plot type selection.
- Time-basis selection.
- Long-report-gap overlay.
- Gap analysis target selection.
- Range start and range end.
- Stem and line toggles.
- Importing comparison data.
- Exporting session data.
- Saving a PNG.

Closing the chart window does not clear the capture session.

---

### 3.7 Common Plot Types

#### X / Y Count vs Time

Each point is one reported DeltaX or DeltaY sample.

Use these plots to inspect axis noise, micro-jitter, abnormal spikes, and fast-swipe pulses.

#### Interval vs Time

Each point is the time difference between the current report and the previous report.

Spikes may come from wireless or USB instability, Windows scheduling, or capture processing pressure.

#### Frequency vs Time

This plot converts timing into a local report-frequency estimate.

It is smoother than raw interval points while still reflecting Raw Input arrival behavior.

#### Velocity vs Time

Velocity plots use CPI to estimate X velocity, Y velocity, X/Y velocity, or path speed.

They are unavailable when CPI is invalid.

#### Interval Histogram

This distribution shows report intervals inside the selected range.

For a nominal 1000 Hz mouse, the ideal interval is about:

```text
1 ms
```

For a nominal 8000 Hz mouse, the ideal interval is about:

```text
0.125 ms
```

Real charts are affected by timing precision, scheduling, firmware, and the host environment.

#### Delta Histograms

DeltaX, DeltaY, and Delta magnitude histograms show movement quantization, bias, and micro-movement output.

If low-speed movement is heavily concentrated at 0 or a few count values, the current DPI, speed, or sensor output is strongly quantized.

#### X-Y Trajectory

This plot draws cumulative X and cumulative Y as a path.

Use it to inspect:

- Straight-line behavior.
- Circle shape.
- Angle correction.
- Curving, axis bias, or abnormal reversals.

---

### 3.8 Time Basis

#### Raw Capture Time

Raw capture time keeps the original capture timeline.

If you pause the session, the pause gap remains on the time axis.

#### Logical Session Time

Logical session time compresses pause gaps.

It is better when you want multiple capture segments to look like one continuous session.

---

### 3.9 Import, Export, And Comparison

The chart window can export the current session as a JSON file.

You can import that file later.

Behavior:

- If the page has no live data, importing opens the file in read-only browsing mode.
- If the page already has live data, importing adds the file as comparison data in the chart.
- The chart supports up to 3 mice in one comparison.
- Importing beyond the limit prompts you to replace the oldest imported comparison.
- **Delete Imported Data** clears imported data and returns to live standby mode.

The JSON format is intended for this app. Avoid editing it manually.

---

### 3.10 Reliable Test Design

For comparisons:

1. Use the same mouse pad and similar USB or receiver environment.
2. Record and enter the correct DPI.
3. Use similar movement patterns, such as slow straight movement, fast horizontal flicks, or circles.
4. Keep capture duration similar.
5. Export a baseline and import it during the next test for direct comparison.
6. Watch charts, data quality, and dropped packet count together.

High-report-rate devices are more sensitive to scheduling and processing pressure. Close heavy background programs when testing them.

---

### 3.11 How It Works

During locked capture, this page records only the selected mouse's Raw Input packets.

The app stores timestamps, DeltaX, DeltaY, and control fields. It then builds:

- Live summaries.
- Motion curves.
- Interval and frequency charts.
- Velocity estimates.
- Distribution histograms.
- X-Y trajectories.
- Long-gap overlays and statistics.

The timeline reflects host-side Raw Input arrival behavior, not the mouse's internal hardware clock.

---

## 4. Sensitivity Matching

### 4.1 What This Page Does

Sensitivity Matching helps make a target mouse feel closer to a source mouse.

It records synchronized Raw Input movement from two mice and calculates a recommended target DPI and scale multiplier.

This page only measures and recommends. It does not change mouse driver or hardware settings.

---

### 4.2 Before You Start

You need:

- Two online mice.
- The source mouse's current DPI.
- The target mouse's current DPI.
- Enough mouse pad or desk space.
- A stable grip and repeatable movement.

The source mouse is the mouse whose feel you want to copy.

The target mouse is the mouse you want to adjust.

---

### 4.3 Basic Steps

1. Enter **Sensitivity Matching**.
2. Click **Start** to open binding and DPI setup.
3. Enter **Source Mouse DPI** and **Target Mouse DPI**.
4. Click **Bind** for the source mouse, then lightly move the source mouse.
5. Click **Bind** for the target mouse, then lightly move the target mouse.
6. After both mice are bound, click **Next**.
7. Place both mice side by side.
8. Click **Start Round 1**.
9. Move both mice together in the same direction and rhythm until both progress bars reach full.
10. Repeat for round 2 and round 3.
11. Read the recommended target DPI and scale multiplier.
12. Use **Copy DPI** when needed.

Each round must be started manually. Switching pages cancels the current unfinished round but does not invalidate completed rounds.

---

### 4.4 Binding Notes

During binding, the app waits for movement from the requested mouse.

Notes:

- A small movement is enough.
- Binding times out after about 10 seconds.
- Source and target cannot be the same physical mouse.
- If a bound mouse disconnects, bind again.
- Changing DPI input or rebinding clears measured results.

If you see a binding conflict, the two slots are identifying the same device. Rebind with a different mouse.

---

### 4.5 Measurement Advice

Recommended movement:

- Place both mice side by side.
- Move them in the same direction and rhythm.
- Keep the path as straight as possible.
- Avoid sudden acceleration, hard stops, and curved paths.
- Make each round long enough.
- Do not let a round drag on too long.

The app computes each round's movement target from source DPI and target DPI. Internally, the target distance is equivalent to about 8 inches worth of path counts per round.

---

### 4.6 Result Fields

#### Recommended Target DPI

This is the DPI the app recommends for the target mouse.

Example:

```text
Recommended target DPI 1320
```

If your mouse software cannot set that exact value, use the nearest available value or use the scale multiplier to adjust in-game sensitivity.

#### Scale Multiplier

The scale multiplier tells you how much to multiply the target mouse's current DPI.

Example:

```text
Scale 0.825
```

If the target mouse is currently 1600 DPI:

```text
1600 * 0.825 = 1320
```

You can also use this multiplier for game sensitivity conversion.

#### Consistency

Consistency describes how close the three rounds are to one another.

Common states:

- **Excellent**: three rounds are very close.
- **Good**: the result is reasonably stable.
- **Fair**: usable, but another measurement is recommended.
- **Poor**: high variation, remeasure.

If consistency is low, check movement consistency, synchronization, and available desk space.

---

### 4.7 Common Failure Reasons

#### Timeout

The round took too long.

Retry with continuous movement and fewer pauses.

#### Too Fast

The round completed too quickly to be considered stable.

Move more slowly and smoothly.

#### Insufficient Packets

There were not enough valid Raw Input packets.

Move farther and make sure both mice produce clear movement.

#### Excessive Curvature

The path was too curved.

Use a straighter movement and reduce wrist rotation.

#### Direction Mismatch

The two mice moved in noticeably different directions.

Realign them and move them together.

#### Path Shape Mismatch

The two paths had different shapes.

Try to move both mice with the same rhythm and path.

#### Unsynchronized

The two mice did not start, stop, or move in sync.

Place them side by side, start together, and stop together.

---

### 4.8 Improving Reliability

Recommendations:

- Align both mice before each round.
- Use medium speed.
- Keep the desk or mouse pad flat and stable.
- Reduce wireless interference where possible.
- If consistency is fair or poor, repeat all three rounds.
- Use **Retry Round** when one round is obviously bad.

After applying the recommended DPI in the target mouse software, use the other pages to verify report rate, trajectory, and speed behavior.

---

### 4.9 How It Works

The app records Raw Input movement from the source and target mice.

For each round, it compares path length, primary direction, overlap, synchronization, and shape similarity in a shared time window.

It estimates:

```text
scale = source movement ratio / target movement ratio
```

After three valid rounds, it uses the median of the three scale values as the final multiplier and calculates the recommended target DPI.

---

## 5. Sensor Angle Calibration

### 5.1 What This Page Does

Sensor Angle Calibration measures the angle between your natural horizontal mouse swipe and the Raw Input trajectory.

Use it to:

- Inspect possible sensor angle offset.
- Measure your natural "horizontal" trajectory with your normal grip.
- Get a reference value for mouse software that supports angle calibration.
- Compare different mice, grips, or surfaces.

This page only recommends an angle. It does not change device settings.

---

### 5.2 Basic Steps

1. Enter **Sensor Angle Calibration**.
2. Click the **Measurement Area** to begin and lock the cursor.
3. Swipe left and right with your normal grip.
4. Complete at least **30** accepted swipes.
5. Right-click or press `Esc` to pause.
6. Check the recommended angle, swipe count, sample count, and stability.
7. Click **Copy Angle** to copy the recommendation.
8. Click **Reset** to clear the session.

After pausing, click the measurement area again to continue the same session and improve confidence.

---

### 5.3 Swipe Advice

Recommendations:

- Use your normal grip.
- Swipe left and right while maintaining what feels horizontal to you.
- Keep each swipe as straight as possible.
- Keep left and right travel reasonably balanced.
- Avoid hard turns, shaking, and sudden acceleration.
- If the result is unstable, keep adding clean swipes.

This page measures the real angle of your current grip and device combination. Deliberately correcting your grip can make the result less useful.

---

### 5.4 Metrics

#### Recommended Angle

The recommended angle is fitted from accepted left and right swipe trajectories.

It appears only after enough valid swipes and usable trajectory quality.

The sign follows the app's display convention. If applying the value in other software makes correction go the wrong way, try the opposite sign and verify.

#### Swipes

The number of accepted left/right swipe strokes.

Use at least 30 before trusting the recommendation.

#### Samples

The number of valid horizontal movement segments.

More samples usually help, but many low-quality samples can still reduce result quality.

#### Stability

Stability is the recent variation of candidate angle values, in degrees.

Simple interpretation:

- Lower is more stable.
- Higher means recent fits disagree more.

---

### 5.5 Trajectory Quality Hints

The page may show hints such as:

- **Too few samples**: complete more swipes.
- **Left/right imbalance**: make both sides more symmetrical.
- **High dispersion**: keep trajectories straighter and steadier.
- **Too many outliers**: reduce shaking and sharp turns.
- **Queue overflow**: system pressure may have affected capture; reset and retest.
- **Good / excellent quality**: keep the current rhythm to confirm the result.

Do not rely on a recommendation if quality remains poor.

---

### 5.6 Using The Recommended Angle

If your mouse driver supports sensor angle calibration, enter the recommended angle there.

Suggested workflow:

1. Record the original setting.
2. Enter the recommended angle.
3. Test straight lines in a game or drawing app.
4. If the direction becomes worse, try the opposite sign.
5. If separate measurements disagree, reset and measure again.

Do not lock in a long-term angle from one short measurement. Run 2-3 independent sessions and check whether they agree.

---

### 5.7 How It Works

The app records Raw Input movement while you swipe left and right.

It splits movement into accepted strokes. A stroke must pass checks for length, horizontal ratio, duration, straightness, and point count.

The app fits left and right stroke angle samples, removes obvious outliers, combines both sides, and produces a recommended display angle.

The recommendation appears after enough accepted swipes. Stability is calculated from recent candidate angle variation.

---

## 6. Global Controls

### 6.1 Page Directory

Click the menu button in the top-left corner to open the page directory.

It switches between:

- Report Rate Test
- Button Double-Click Test
- Sensitivity Matching
- Sensor Angle Calibration
- Mouse Performance Analysis

Press `Esc` or click an empty area to close the page directory.

Switching pages may pause or cancel an active capture depending on the page.

---

### 6.2 Language And Theme

The top bar and page directory provide language and theme toggles.

Language can switch between Chinese and English.

Theme can switch between light and dark.

These settings only affect the interface. They do not affect Raw Input capture or test results.

---

### 6.3 Leaving Locked Capture

Several capture pages use the same exit methods:

- `Esc`
- Right-click
- The page's start/pause shortcut
- Leaving the window or switching pages

When a page is locked, mouse input is being used for the current test. After leaving lock, the cursor returns to normal behavior.

---

## 7. General Notes

### 7.1 What Raw Input Results Mean

ClickSyncMouseTester observes data received at the Windows Raw Input layer.

It reflects the current PC, operating system, driver, USB or wireless path, and application processing environment.

It is not a hardware bus analyzer and does not read the mouse's internal sensor clock.

Interpret results as:

> What a Windows user-mode application can actually receive from the mouse in the current environment.

---

### 7.2 Factors That Affect Results

Common factors:

- Mouse firmware and driver settings.
- Report rate, DPI, and power-saving modes.
- USB ports, docks, and hubs.
- Wireless receiver distance and interference.
- System load, background apps, and screen recording.
- Display refresh rate and desktop-composition load.
- Mouse pad surface and movement method.

When results look abnormal, change one variable at a time and retest.

---

### 7.3 Recommended Testing Habits

For better results:

- Confirm the selected device before every test.
- Use the same DPI, report rate, and movement method for comparisons.
- Close unnecessary background apps during high-report-rate tests.
- Record mouse model, port, driver settings, and environment.
- Do not judge only by one instant value. Use peak values, distributions, charts, and quality hints together.
- If dropped packets or degraded quality appear, check system load and connection environment first.

These habits make results easier to compare and reproduce.
