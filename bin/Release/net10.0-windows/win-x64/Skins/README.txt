FURI//CORE SKIN FOLDERS

Choose any folder from SKIN FOLDER or ASSET MANAGER.
FURI//CORE looks for these filenames and uses whatever exists:

idle.png
talk.png
thinking.png
thinking_talk.png
deadpan.png
deadpan_talk.png
shocked.png
hype.png
hype_talk.png
rage.png
rage_talk.png
sleepy.png
sleepy_talk.png
laugh.png
laugh_talk.png

A folder can be partial. Missing files fall back to the built-in Chaos Critter.
For paired reactions such as Laugh and Sleepy, a missing *_talk.png automatically
reuses that reaction's REST image, including a custom REST override if one exists.
Per-emotion file choices made in ASSET MANAGER override the skin folder.

Priority:
1. Per-emotion file override
2. Selected skin folder
3. Built-in Chaos Critter
