# Third-party components

The project references these NuGet packages when built:

- NAudio 2.3.0 — audio capture and processing plumbing.
- Vosk 0.3.38 — offline streaming speech recognition bindings.

The optional English trigger model is downloaded at runtime from the official Vosk model host:

`vosk-model-small-en-us-0.15`

Review each upstream project's license before redistributing a compiled binary or bundling models in a public release.
