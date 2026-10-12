# FURI//CORE v0.7.3 — Suppression Latch + Signal Clarity

This build fixes the live personalized Reaction Trainer classifier introduced in v0.7.1.

## v0.7.3 focus
The raw Event Sense meter is allowed to move when a fan, AC, keyboard, or room sound reaches the physical mic. Reactions are controlled by the classifier, not by raw meter movement. The Event Sense status line now shows the current top class (for example `NOISE 100%`).

This build also prevents a detector lock that was suppressed during normal conversation from firing after the conversation guard expires. It must release and produce a fresh lock first.

## What was wrong

WASAPI can deliver the microphone stream in packets smaller than the requested 40 ms buffer. v0.7.1 passed each callback directly to the classifier, which only analyzed complete 640-sample / 40 ms frames. Smaller packets were discarded instead of being joined together. The training profile could therefore show PERSONAL mode while producing no live classifications.

## What changed

- Event Sense PCM packets are accumulated continuously.
- Every complete 640-sample / 40 ms frame is analyzed; leftover samples are retained for the next callback.
- The classifier now exposes packet/sample/frame/classification counters.
- Starting DEBUG writes `PERSONAL_CLASSIFIER_SNAPSHOT` immediately.
- Once warm, DEBUG should also contain regular `CLASSIFIER` rows.

## Recommended test

Use split routing:

- **Voice / Words:** Krisp
- **Event Sense:** raw physical microphone

Make sure the main window says `PERSONAL` for Event Sense. Turn DEBUG on, then do a short sequence: 10 seconds of typing only, a few seconds of normal speech, a natural laugh, a yawn, and a normal `hmmm`.

The CSV should now contain a snapshot similar to:

`PERSONAL_CLASSIFIER_SNAPSHOT ... ready=True; packets=...; frames=...; classifications=...`

and repeating `CLASSIFIER` rows showing `top=Keyboard`, `top=Normal`, `top=Laugh`, `top=Yawn`, or `top=Hmm`.

## Training

Training remains local under `%LOCALAPPDATA%\FuriXeus\Tuber\Training\`. Existing v0.7.0/v0.7.1 WAVs are reused. You do not need to retrain just for this bug fix.

## Building

Run `CLEAN_REBUILD.bat` from the project root.

Requirements:

- Windows 10/11
- .NET 10 SDK for compilation
- Internet access during NuGet restore and initial Word Core installation

## Existing features retained

- Dual Voice/Words and Event Sense inputs
- Vosk Word Core trigger phrases
- Conversation Guard
- Reaction Trainer with Normal / Laugh / Yawn / Hmm / Noise / Keyboard samples
- Debug CSV logging with build IDs
- OBS-friendly output
- Skin folders and per-emotion Asset Manager overrides
- Separate REST/TALK slots for Thinking, Deadpan, Hype, Rage, Laugh, and Sleepy