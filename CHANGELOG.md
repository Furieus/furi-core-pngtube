# Changelog

## v0.7.3 — Suppression Latch + Signal Clarity
- Fixes a race where a personalized Hum/Yawn/Laugh lock could be correctly suppressed during normal speech, then fire milliseconds later after the conversation guard expired while the same classification was still fresh.
- Suppressed acoustic locks are now latched off until that detector fully releases; a new reaction requires a new rising edge.
- Adds debug events when a suppression latch clears.
- Clarifies Event Sense UI: the meter is explicitly the RAW MIC and can move for fan/room noise; the status line now shows the personalized classifier's current top label and confidence.
- Keeps v0.7.2 streaming buffering, personalized training, Word Core, dual-input routing, OBS output, and asset manager behavior.
