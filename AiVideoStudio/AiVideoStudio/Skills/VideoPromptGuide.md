# Video Prompt Writing Guide (T2VA / I2VA / FL2VA / L2VA)

## 1. Task Overview

- T2VA: Builds a complete audiovisual timeline from text.
- I2VA: T2VA body + first-frame instruction + a visual path that develops forward from the first frame.
- FL2VA: T2VA body + first-and-last-frame instruction + a continuous path from the first frame to the last frame.
- L2VA: T2VA body + last-frame instruction + a path that converges from a plausible preceding state to the last frame.

## 2. Final Prompt Structure

### 2.1 Part One Is the Instruction

T2VA has no image-alignment instruction and begins directly with the three core fields.

I2VA always uses:

```
For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.
```

FL2VA always uses:

```
How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot N) aligns with the S.SS-second mark of the target video.
```

L2VA always uses:

```
How the reference pictures align with the target video — <Picture 1> (from [Shot N]) aligns with the S.SS-second mark of the target video.
```

Here, N is the index of the actual final shot, and S.SS is the effective video duration formatted to exactly two decimal places. The instruction must be the first line of the final prompt, followed by one blank line before the core fields.

### 2.2 Part Two Contains the Three Core Fields

```
integrated_multimodal_description: [Shot 1] ...
overall_soundscape: ...
non_diegetic_music: ...
```

- integrated_multimodal_description: Describes visuals, actions, shots, speakers, dialogue, singing, and diegetic audio along the timeline.
- overall_soundscape: Summarizes ambient sound, physical action sounds, and non-verbal human sounds across the entire video.
- non_diegetic_music: Describes background music that the characters cannot hear and only the audience can hear.

## 3. How to Incorporate Keyframes into the Multimodal Description

### 3.1 I2VA: Begin from the Image and Develop Forward

<Picture 1> is the actual first frame of the video at 0.00 seconds and belongs to [Shot 1]. First establish style, subjects, composition, and scene anchors in the image, then describe the next action. Keep character identity, clothing, colors, key objects, and spatial relationships consistent.

Recommended structure: first-frame anchor → action onset → continuous development → result or reaction.

### 3.2 FL2VA: Describe the Path Between the First and Last Frames

Picture 1 is the opening, Picture 2 is the ending. Focus on how the subject moves, poses change, objects are manipulated, composition evolves, and scene or lighting transitions. FL2VA generally favors a single shot; use multiple shots only when explicitly specified. The last frame must be reached by the final [Shot N].

Recommended structure: first-frame state → observable intermediate changes → progressively narrowing differences → last-frame state.

### 3.3 L2VA: Infer the Opening and Land on the Image at the End

<Picture 1> is the final frame and belongs to the last [Shot N]; it does not inherently belong to Shot 1. Infer a plausible earlier state, then describe how characters, objects, camera, and scene gradually approach the reference image.

Recommended structure: plausible preceding state → explicit action and transition path → gradual convergence in the final shot → last-frame landing.

## 4. How to Write the Three Shared Core Sections

### 4.1 Develop the Multimodal Description Along the Timeline

Every detail should correspond to something visible or audible: visual style, initial composition, subject appearance and position, scene and key props, actions and reactions, shot changes, spoken language, and synchronized diegetic sound. At the beginning of [Shot 1], state the overall style and initial composition (Cinematic, live-action, 2D-animated, 3D CG, claymation, watercolor, vintage film; for keyframe tasks derive style from the reference image).

### 4.2 Shots and Cuts

Do not add a timestamp to the first shot. Later shots use sequential numbers with a strictly increasing cut time within the video duration: `[Shot 2] At 00:03.500, the camera cuts to...`. Use ordinary cuts (cuts to, transitions to, changes to, switches to); cross-dissolve, fade, or wipe only when explicitly requested.

### 4.3 Camera Motion: Motion Type + Amplitude + Speed

Available motion types: Zoom In / Zoom Out, Push In / Pull Out, Pan Left / Pan Right, Truck Left / Truck Right, Tilt Up / Tilt Down, Pedestal Up / Pedestal Down, Arc Shot, Tracking Shot, Static Shot, Shake Slightly / Shake Strongly, POV, Roll Clockwise / Roll Counterclockwise. Amplitude: with small amplitude / with large amplitude. Speed: at slow speed / at fast speed. Write as natural English within the shot: `The camera pushes in with small amplitude at slow speed toward the folded letter in her hands.`

### 4.4 Speakers, Dialogue, and Singing

Speakers use stable IDs (S1), (S2); joint speech uses compound IDs (S1,S2). A speaker keeps the same ID across shots; characters who never vocalize receive no speaker ID. On first appearance, establish identity (character type, age, gender, on-screen or not, pitch, timbre, rate, accent). Place the identifying phrase, ID, action, and delivery OUTSIDE <d>. Inside <d>, include ONLY the language tag and the actual spoken content, preserved verbatim — do not translate or rewrite:

```
The young woman with a quiet, breathy voice (S1) says: <d>[English] I get off at the next station.</d>
The two children (S1,S2) shout together, <d>[English] Wait for us!</d>
```

Voiceover uses the exact phrase `says in an off-screen voiceover`, and the on-screen character's lips remain closed:

```
The man (S1) says in an off-screen voiceover: <d>[English] I still remember that road.</d> while his lips remain completely closed.
```

Dialogue crossing a cut uses <scenetrans> in both parts with audio continuing across the cut. Use <cutoff> when speech is truncated by the video end.

### 4.5 On-Screen Text

Visible banners, signs, labels, subtitles, or neon text go in English double quotation marks, verbatim, without translation.

### 4.6 overall_soundscape

1–4 English sentences, one paragraph: ambient sound, physical action sounds, non-verbal human sounds. Do NOT repeat dialogue/singing/diegetic music here. Use N/A only when the user explicitly requests complete silence.

### 4.7 non_diegetic_music

1–3 English sentences: instrumentation, speed, rhythm, dynamic changes; no abstract mood words. Singing/instruments audible to characters are diegetic and belong in the multimodal description. Use N/A when there is no non-diegetic music.

## 5. Cases

T2VA (no reference image), I2VA (first-frame instruction + develop forward), FL2VA (two-image alignment + motion path between them, single 8s shot example), L2VA (final-frame alignment + converge from a plausible opening, single 6s shot example) — see the full guide examples for each pattern.

# Full-Reference Mode Rewrite Output Format Guide

Write all six rewrite sections in English. Preserve the original language only for dialogue and lyrics inside <d> and for text visibly present in the scene. Make detailed_description as detailed and explicit as possible per shot (composition, appearance, position, environment, lighting, actions, state changes, camera movement, current sound, reference appearance points). Shot, camera, speaker, dialogue, and sound formats are shared with the T2VA guide above.

## 1. Overall Structure (in order)

1. subject_definitions — referenced content and labels
2. summary — task type, target video, main reference relationships
3. retention_analysis — how referenced content is preserved/transferred/reused
4. detailed_description — visuals, actions, shots, sound, dialogue in playback order
5. overall_soundscape — ambience and physical sounds
6. non_diegetic_music — audience-only background music

## 2. Reference Labels

- <Subject N>: reusable visible content (people, animals, objects, scenes, clothing, props, styles, actions, expressions, poses). `<Subject 1> is the young woman in <Picture 1>, with long dark hair, a blue cardigan, and a thin silver necklace.`
- <Picture N>: a reference image as first frame / keyframe / last frame / composition anchor. Cite inside a <Subject N> definition instead if the image only defines a character/scene/costume/style.
- <Video N>: whole-video relationships only (edit source, continuation starting point, camera/cut/rhythm structure). `<Video 1> is the source video for the target video edit.`
- <Audio N>: standalone audio asset or enabled sync track (copy, music-style reference, voice-timbre reference, dialogue reuse, beat/rhythm reference). `<Audio 1> is the voice-timbre reference for <Subject 1> (S1).`
- <Video N> and <Audio N> are numbered independently; the same file may be <Video 1> and <Audio 2>. An ordinary reference video does not create <Audio N> merely because it contains sound.

## 3. summary

One short English paragraph starting with a square-bracketed task-type prefix: `[reference generation]`, `[video editing + reference generation + audio reuse]`, `[video continuation + keyframe completion]`, etc. Task types: keyframe completion, reference generation, video editing, video continuation, audio reuse, audio reference. Reuse defined labels; introduce no new ones. For edits begin: `The target video is an edited version of <Video 1>.`

## 4. retention_analysis

One line per reference label with fixed markers. Visible content: fully_preserved, partially_preserved, attribute_transfer, weak_reference (e.g. `<Subject 1> (appears in [Shot 1], [Shot 3]): fully_preserved - ...`). Audio: fully_copy, partially_copy, reference, weak_reference (e.g. `<Audio 1>: fully_copy - <Audio 1> is reused 1:1 as the target video's complete final audio track.`).

## 5. detailed_description

English body, 350–500 words for generation tasks (dialogue-dense content prioritizes the complete spoken timeline over word count). [Shot 1] has no timestamp; later shots use `[Shot N] At MM:SS.mmm, ...`. Establish style in one or two sentences before [Shot 1]. Insert <Subject N>/<Picture N>/<Video N>/<Audio N> at first appearance and where roles apply. Speakers: `<Subject 2> (S1) turns toward the woman and says, <d>[English] Last summer, I went to my grandfather's house.</d>` — (Sx) follows target-video vocal order, assigned once and reused; never assign new IDs in audio definitions or retention_analysis. Verbal cues existing only inside reused BGM use <Audio N>, not (Sx). Reused dialogue preserved verbatim; `[unclear]` for unintelligible spans; standardize punctuation to `,.?!`.

## 6. overall_soundscape and non_diegetic_music

As in the T2VA guide. Write full dialogue/lyrics ONLY inside <d> in detailed_description, never repeated here. When reference audio is used, state its copy/reference relationship in the matching audible layer (ambience in overall_soundscape, score in non_diegetic_music).
