# HairBrush MCP

Lets Claude (or any MCP host) groom hair in a running HairBrush: load a head, make groups, scatter cards over regions of the scalp, set lengths/bends/curls, comb with guides, clump, take screenshots, undo, save and export.

```
Claude ⇄ stdio (MCP) ⇄ tools/hairbrush-mcp/server.mjs ⇄ 127.0.0.1:5170 ⇄ HairBrushMcpServer (in the app)
```

- **Unity side**: `Assets/Scripts/MCP/`
  - `HairBrushMcpServer` owns the socket. It spawns itself like every other authority does.
  - `HairBrushMcpCommands` does the grooming through the same entry points the UI uses: `PinHairCard`, the slider handlers, `CreateGuide`, the variance and curve registries, and project IO. Symmetry, POSTs, root state and the group panel therefore all behave as normal.
  - Every tool call becomes one undo step.
- **Bridge**: `tools/hairbrush-mcp/server.mjs`. It needs Node 18+ and nothing else; there is nothing to `npm install`.

## Running

1. Start HairBrush with the listener on. Either:
   - open the project in Unity and press **Play** (the editor always listens), or
   - start a built player with `HairBrush.exe -mcp`, or set `HAIRBRUSH_MCP=1`.

   Without one of these, a shipped build never opens the port.
2. Run Claude Code from the repository root. `.mcp.json` registers the `hairbrush` server. Approve it the first time.
3. Ask for a hairstyle.

The first side to start creates the shared token at `%USERPROFILE%\.hairbrush-mcp\token`, and the other side reads it. Keep it private.

Optional environment variables, which must match on both sides:
- `HAIRBRUSH_MCP_PORT` (default `5170`)
- `HAIRBRUSH_MCP_TOKEN_FILE`

Check the link from a terminal:

```powershell
node tools/hairbrush-mcp/server.mjs --call hb_status
node tools/hairbrush-mcp/server.mjs --call hb_screenshot '{"view":"front"}' --out shot.png
```

## Head frame

Points on the scalp are given as `az`/`el` degrees, measured from the centre of the head's bounds:
- `az 0` is the face, `+90` the character's right, `-90` their left, `180` the back.
- `el +90` is the crown; negative values are below the centre, toward the nape.

`hb_head_info` returns landmark positions. Imported heads are normalised to about 0.33 units wide, so a card length of `0.2` is roughly shoulder length.

## Tools

| Area | Tools |
|---|---|
| Inspect | `hb_status`, `hb_head_info`, `hb_probe_points`, `hb_list_groups`, `hb_get_group` |
| Files | `hb_load_model`, `hb_load_project`, `hb_save_project`, `hb_export_obj` |
| Groups | `hb_create_group`, `hb_select_group`, `hb_rename_group`, `hb_delete_group`, `hb_set_group_flags` |
| Cards | `hb_place_cards`, `hb_fill_region`, `hb_erase_cards`, `hb_set_group_params`, `hb_set_variance`, `hb_set_shape_curve` |
| Modifiers | `hb_add_guide`, `hb_edit_guide`, `hb_remove_guide`, `hb_add_clumper`, `hb_edit_clumper`, `hb_remove_clumper` |
| Material and UVs | `hb_set_hair_material`, `hb_get_uv_rects`, `hb_set_uv_rects`, `hb_set_group_uv` |
| POSTs | `hb_add_post`, `hb_edit_post`, `hb_remove_post` |
| View and history | `hb_set_symmetry`, `hb_set_view`, `hb_screenshot`, `hb_undo`, `hb_redo` |

Not yet exposed: per-group materials, texture loading, POST-local shape curves, and remap.

## Notes

- Loading a model or project refuses to replace an existing groom unless `discard_groom=true` is passed.
- Saving refuses to replace an existing file unless `overwrite=true` is passed.
- After a timeout or a dropped connection the edit may still have happened. Check with `hb_status` before retrying.
- `hb_screenshot` renders with the main camera from a temporary pose and does not move the user's view. `include_ui=true` grabs the real window instead.

## Tests

```powershell
node --test tools/hairbrush-mcp/test.mjs
```
