# HairBrush grooming guide (for MCP agents)

The method for building a good groom through the `hb_*` tools. It combines the HairBrush manual (BETA 0.3) with what worked and what failed in practice. Read this once before grooming.

## Principles

- **The texture does the detail; the cards carry the shape.**
  - Use few, wide cards (width 0.015–0.03) with low segment counts (4 for short hair, 6–8 for long).
  - Thousands of thin cards look flat and cost polygons.
  - Watch the POLYGONS figure; a good short men's cut is ~10–25k triangles.
- **Use few groups, and shape them with POSTs.**
  - A group is a set of cards that share one set of settings, so make one per layer or texture type: for example Sides, Top, and maybe a Fringe or Flyaways layer.
  - Do regional changes (longer at the front, shorter at the temples, lifted crown) with POSTs inside the group, not with more groups.
- **Use predetermined UVs (`hb_set_group_uv`), not manual U/V sliders.**
  - Each card gets a seeded strip from the group's min_id–max_id range, which breaks up repetition.
  - Inspect the strips first with `hb_preview_uv_rects` and choose ranges on purpose:
    - Dense, blunt strips for the base.
    - Tapered, separated strips for the top and outer layer.
    - Wispy strips for flyaways and hairline edges.
- **Small variance goes a long way.** About ±8–15° on AngleY/Z, a little Length, and Twist ±15–25° stops it looking manufactured. Too much looks messy.
- **Review from all sides, every time.** Use `hb_turnaround` with `lighting: "studio"`. Use `overlay: true` when you need to see the modifiers, and `scalp_check: true` to find coverage gaps.

## Units and the head frame

- Positions are given as `az`/`el` in degrees around the head centre:
  - `az` 0 is the face, +90 the character's right, -90 their left, 180 the back.
  - `el` +90 is the crown.
- The head bounds include the neck and bust, so measure the scalp with `hb_probe_points` rather than assuming.
  - On the demo head the skull radius is about 0.08–0.1 and the top of the head is at y ≈ 0.18.
  - On that head the natural front hairline sits at about el 48 at az 0, receding to el 55–57 at the temples.
  - The ear spans roughly az 92–116, el 15–42.
- Lengths are in world units, so on that head 0.02 is short-sides length and 0.08–0.1 is a quiff.
- Variance and POST deltas use each channel's own units: world units for Length and Width, degrees for the angles.

## Card parameters

- **Angle X** tilts the card off the surface normal. 0 stands straight out; 75–85 lays it along the scalp, which is right for short sides.
- **Bend** curves along the length.
  - Positive bend curls toward the scalp, so short cards hug the head.
  - A rough fit to the skull's curve is `bend ≈ length / skull_radius` in radians, converted to degrees. For example, length 0.022 at radius 0.08 gives about 16°.
- **Arch** is the cross-section: 0 is a flat ribbon, 0.5 normal, 1 strongly cupped. Cupped (0.8–1) side cards read as rounded clumps instead of flat scales.
- **Segments:** 4–60. Fewer is cheaper. Use the SegmentDensity profile to push segments toward the root for a smooth bend near the scalp.
- **Width profile** (`hb_set_shape_curve` channel Width) can only narrow the card.
  - Taper long hair, for example `[[0,1],[0.6,0.9],[1,0.45]]`.
  - Keep short hair almost square, e.g. `[[0,1],[1,0.9]]`, and let the texture's alpha make the edge.
- **Embed depth:** 0.0005–0.001 sinks the root so it never floats.

## Quiffs and lift (tested)

Lift for a quiff, pompadour or any hair that stands up comes from the **cards' own angle and bend**, applied regionally with a POST. Guides cannot provide it.

- **Guides don't give lift.** Guides at full amount scale the cards' own bend and angles away, and a guide arc only eases the root third of each card. So making the guide curves dramatically taller barely changes the silhouette.
- **What works:** a front POST with `angle_x` -50 to -60, `bend` -40 to -50 and length +0.02–0.03 (radius ~0.045, falloff ~0.04) rises off the hairline and rolls back over the head. Turn the front guides down to amount ~0.3 so they only steer direction.
- **Sign reference** (front hairline, cards standing out of the surface):
  - Negative `angle_x` tilts up.
  - Positive bend curls the tips forward and down (a fringe).
  - `angle_x` -40 with no bend makes vertical spikes.
  - `angle_x` -30 with bend +90 makes bangs falling over the face.

## Placement

- **`hb_fill_region`** is the EVEN brush over an az/el region: cards are kept at least `spacing` apart, including from existing cards and from symmetry mirrors.
  - With symmetry on, fill only az 0..180 and the mirror covers the other side.
  - **Hairlines:** use `lower_edge` (the hairline shape as [az, el] points), `edge_jitter` 2–4°, and `edge_falloff` 4–8° so the edge thins out instead of ending in a visor-like line.
  - **Typical spacing:** 0.006–0.009 for wide cards. Denser is not better; check with `scalp_check`.
- **`hb_place_cards`** places single cards precisely.
- **`hb_erase_cards`** cleans up strays, such as cards below the nape or caught on the ear.

## Modifiers

### POST (localized manipulator)

- A soft sphere: full effect within `radius`, fading out over `falloff`. The app's defaults are radius 0.025 and falloff 0.04.
- **REL** (relative, the default) adds `delta` to each card, so variance survives. **ABS** (absolute) forces baseline + delta and flattens variance.
- Overlapping POSTs combine in creation order.
- Use `mirror: true` for the other side; the lean channels are negated so the copy mirrors properly.
- Typical uses:
  - **Fade:** negative length and width deltas at the nape, around the ears and at the sideburns.
  - **Quiff:** positive length and lift at the front.
  - **Crown:** shorter, flatter.
  - **Parietal ridge:** shorter, plus a positive bend so the top tucks onto the sides.

### GUIDE (comb in parallel)

- Hair near the curve lies along it while keeping its own root.
  - Roughly the bottom third of each card eases onto the curve.
  - Length never changes.
  - A card longer than the guide carries straight on in the guide's last direction.
- **Bend, the angles and the profile curves are scaled down by `amount`.**
  - At 1 the guide fully owns the shape; at 0.5, half the card's own bend and angle remain.
  - For lift that comes from the card itself (bend or angle), use a lower amount (0.4–0.7).
- **Shape guides with `flow`.** It traces the skull, so the curve follows the head.
  - `direction`: "back" for swept-back hair, "down" for hanging hair, "left"/"right" for a side part.
  - `length` should be at least the card length.
  - `lift` is the height off the scalp: 0.004 lies flat, 0.02 gives volume, 0.04+ gives a quiff or pompadour.
  - `peak_at` is where the height peaks (0.1 lifts at the root; 0.3–0.4 rolls it).
  - `tail` is the tip height as a fraction of `lift`.
  - `up_bias` 0.5–0.8 at the front hairline makes hair rise rather than jut forward over the brow.
- Use a light grid: about 3 guides across the front, 4 across the mid-top and 3 at the crown, each with `mirror: true`. Radius 0.035–0.05 with falloff about 0.04.

### CLUMPER

- Gathers cards into strands, for styled or textured looks.
- **Modes:** DispersedEvenly (`count` clumps through the group) is the textured-top look; Singular is a single piece; FromPoint gathers clumps around one spot.
- Amount 0.3–0.5 is usually enough. Higher opens gaps in the scalp; check with `scalp_check`.
- Use `scope: "contig"` so scalp hair doesn't clump with ear or beard hair.

## Material

- `hb_set_hair_material`:
  - The tint multiplies the strand texture; white leaves it as authored. Dark brown is about [0.25, 0.19, 0.14].
  - Smoothness about 0.45–0.55.
- `hb_set_card_style` sets the cross-section (Tent or Diamond) and topology for all cards.

## Workflow

1. `hb_status`, then `hb_head_info`. `hb_probe_points` to measure the hairline, ears and nape on this particular head.
2. `hb_preview_uv_rects`: decide which strip ids each layer uses. If the rects are missing or poor, use `hb_set_uv_rects` (with `auto_detect` or explicit rects).
3. `hb_set_symmetry` on, then `hb_set_hair_material` for colour.
4. **Base and sides layer:** create the group, `hb_fill_region` with `lower_edge`, then `hb_set_group_uv`, params, a little variance, and fade POSTs. Run a turnaround.
5. **Top layer:** create the group, fill it with a hairline edge, set UVs and a width taper, add flow guides (mirrored), then clumpers, then POSTs for the regional shape. Run a turnaround.
6. **Review loop:**
   - `hb_turnaround` (studio lighting).
   - `scalp_check` for gaps.
   - `overlay` to see what drives a problem area.
   - `hb_sample_cards` to check actual values.
   - Fix one thing at a time.
7. **Optional:** a sparse flyaway or edge layer with wispy strips and higher variance, to soften the silhouette and hairline.
8. `hb_save_project` at every milestone. Use `hb_batch` for multi-step edits so each idea is one undo step.

## Pitfalls seen in practice

| Problem | Cause | Fix |
|---|---|---|
| Visor-like hairline | `fill_region` ending at a constant elevation | Use `lower_edge` with jitter and falloff |
| Hair jutting over the brow | Guide lift along the normal at the front | Use `up_bias` |
| Quiff with no height | Guide amount 1 cancelling the cards' bend and angle, or cards longer than the guide carrying straight on | Lower the amount, or make the guide longer |
| Scales or feathers on short hair | Whole long strips squashed onto short cards | Use short-hair rects cut from the root end of strips, and arch 0.8–1 |
| Ledge between the top and sides | Missing parietal tuck | Shorten the top there with a POST and add positive bend |
| Bowl cut | Hairline set too low (el about 37 instead of about 48 on the demo head) | Measure the hairline first |
| A group's cards jump to length 0.2 | The root-state authority still held the new group's slider defaults (fixed in the tools; check with `hb_sample_cards` if lengths look wrong) | `hb_set_group_params` with the intended values |
| Lost work after a recompile | Leaving Play mode wipes the scene | `hb_load_autosave` |
