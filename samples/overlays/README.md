# Test overlays

Pictures to try the overlays of the layout page with. Upload them from **Layout → Overlays and
images**, or drop them on the canvas.

| File | What it tests |
| --- | --- |
| `orbis-test-overlay.png` | A 1920×1080 PNG with alpha: hard edges (frame, chips), antialiased text, and a soft fade at the bottom (alpha that is neither 0 nor 1). It is a still, so the live decodes it once. |
| `orbis-test-overlay-animated.webm` | The same overlay as a 4 second VP9 WebM with alpha, a seamless loop at 30 fps. The lower third prints the frame number: a live that drops or repeats frames shows it there. Opened through libvpx, which keeps the alpha the native decoder drops. |
| `orbis-test-logo.png` | A 400×400 logo with a transparent background: it lands at its own size instead of filling the canvas. |

What to look for on air:

- **The corner ticks** are on the very edge of the frame: a cropped or shifted overlay loses them.
- **The dashed rectangle** is the 5% safe area: on a 720p canvas it stays where it is, only smaller.
- **The webcam frame** prints its own rectangle, `CAM · 480×270 @ 1376,600`: a slot of that size at
  that position sits exactly inside it, under the frame.
