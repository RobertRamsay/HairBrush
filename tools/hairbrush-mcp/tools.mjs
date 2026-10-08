// HairBrush MCP tool catalogue. Each tool `hb_<name>` is forwarded to the running app as the
// command `<name>`; the schemas here are the first line of validation, the app re-checks every
// argument itself.

const num = (description, extra = {}) => ({ type: 'number', description, ...extra });
const int = (description, extra = {}) => ({ type: 'integer', description, ...extra });
const bool = description => ({ type: 'boolean', description });
const str = (description, extra = {}) => ({ type: 'string', description, ...extra });
const vec3 = description => ({ type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3, description });
const groupId = int('Group id. Defaults to the currently selected group.', { minimum: 0 });

const point = {
  type: 'object',
  description: 'A point on the scalp: either az/el in the head frame, or a world position (snapped to the surface).',
  properties: {
    az: num('Azimuth in degrees: 0 = face, +90 = character\'s right, -90 = left, 180 = back.'),
    el: num('Elevation in degrees above the horizontal through the head centre: +90 = crown, negative = below centre (nape).'),
    position: vec3('World position, as an alternative to az/el.'),
  },
  additionalProperties: false,
};

const cardParams = {
  type: 'object',
  description: 'Hair card parameters (the grooming sliders). Units are world units; head width is ~0.33.',
  properties: {
    length: num('Card length in world units (0.2 is roughly shoulder length).', { minimum: 0.0001 }),
    width: num('Card width (slider range 0.0005 to 0.05; default 0.01).', { minimum: 0.0005, maximum: 0.05 }),
    segments: int('Segments along the card (4 to 60).', { minimum: 4, maximum: 60 }),
    embed_depth: num('How far the root sinks into the scalp (0 to 0.1).', { minimum: 0, maximum: 0.1 }),
    bend: num('Bend angle in degrees (-360 to 360). Curves the card along its length.'),
    twist: num('Twist angle in degrees (-360 to 360).'),
    angle_x: num('Root rotation about X in degrees: tilts the card away from the surface normal (e.g. 60-90 lays hair flat along the scalp).'),
    angle_y: num('Root rotation about Y in degrees.'),
    angle_z: num('Root rotation about Z in degrees.'),
    curl_frequency: num('Curl frequency (-10 to 10).'),
    curl_diameter: num('Curl diameter (0 to 0.15).', { minimum: 0 }),
    wave_amplitude: num('Wave amplitude (0 to 0.03).', { minimum: 0 }),
    wave_frequency: num('Wave frequency (-10 to 10).'),
    wave_direction: num('Wave direction blend (0 to 1).', { minimum: 0, maximum: 1 }),
    arch: num('Card cross-section arch (0 to 1, 0.5 neutral).', { minimum: 0 }),
    u_scale: num('Texture U scale (-1 to 1).'),
    v_scale: num('Texture V scale (-1 to 1).'),
    u_offset: num('Texture U offset (0 to 1).'),
    v_offset: num('Texture V offset (0 to 1).'),
  },
  additionalProperties: false,
};

// POST deltas: same channels, but offsets may be negative, so no range limits.
const deltaParams = {
  type: "object",
  description: "Per-channel offsets added to the card values (same names/units as card params; angles in degrees, lengths in world units). Negative values allowed.",
  properties: Object.fromEntries(Object.entries(cardParams.properties).map(([k, v]) => [k, { type: v.type, description: v.description }])),
  additionalProperties: false,
};

const regionProps = {
  az_min: num('Start azimuth (degrees). If az_min > az_max the range wraps through the back, e.g. 120..-120.'),
  az_max: num('End azimuth (degrees).'),
  el_min: num('Lowest elevation (degrees).'),
  el_max: num('Highest elevation (degrees).'),
};

const VIEWS = ['current', 'front', 'back', 'left', 'right', 'top', 'three_quarter_right', 'three_quarter_left', 'back_three_quarter_right', 'back_three_quarter_left'];
const SHAPE_CHANNELS = ['Bend', 'X', 'Y', 'Z', 'CurlFrequency', 'CurlDiameter', 'SegmentDensity', 'Width', 'WaveAmplitude', 'WaveFrequency', 'WaveDirection'];
const VARIANCE_CHANNELS = ['Length', 'Width', 'Bend', 'Twist', 'AngleX', 'AngleY', 'AngleZ', 'CurlFrequency', 'CurlDiameter', 'WaveAmplitude', 'WaveFrequency', 'WaveDirection', 'Arch'];

const obj = (properties = {}, required = []) => ({ type: 'object', properties, required, additionalProperties: false });
const ro = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const rw = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const destructive = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };

const guideShape = {
  nodes_world: { type: 'array', items: vec3('Node'), minItems: 2, maxItems: 20, description: 'Explicit guide nodes in world space, root to tip (root excluded). Overrides direction/length.' },
  direction: { description: 'Flow direction: down/up/front/back/left/right (head frame, character\'s left/right) or an [x,y,z] world vector.', anyOf: [{ type: 'string', enum: ['down', 'up', 'front', 'back', 'left', 'right'] }, vec3('Vector')] },
  length: num('Guide length in world units.', { minimum: 0.01 }),
  lift: num('How far the strand rises off the scalp before turning, as a fraction of length (default 0.15).', { minimum: 0, maximum: 1 }),
  node_count: int('Number of nodes generated from direction/length (2-20, default 4).', { minimum: 2, maximum: 20 }),
  amount: num('Combing strength 0-1 (default 0.8 on creation). 0 = no effect.', { minimum: 0, maximum: 1 }),
  radius: num('Full-strength radius of influence (0.001-0.25).'),
  falloff: num('Falloff distance beyond the radius (0-0.25).'),
  spin: num('Spin of the cards about the guide in degrees.'),
};

const clumperProps = {
  mode: str('Clump mode.', { enum: ['Singular', 'DispersedEvenly', 'FromPoint'] }),
  amount: num('Clump strength 0-1 (default 0.6 on creation).', { minimum: 0, maximum: 1 }),
  count: int('Number of clumps for DispersedEvenly (1-24).', { minimum: 1, maximum: 24 }),
  seed: int('Random seed.'),
  radius: num('Full-strength radius.'),
  falloff: num('Falloff distance.'),
};

export const TOOLS = [
  { name: 'hb_status', description: 'Connection and session overview: is a head loaded, groups with card/guide/clumper counts, current group, symmetry. Call this first.',
    inputSchema: obj(), annotations: ro, timeout: 15000 },
  { name: 'hb_head_info', description: 'The head frame (centre, size, front/right/up axes) and surface landmarks (crown, hairline, temples, sides, back, nape) as az/el plus world positions. Use it to plan where hair goes.',
    inputSchema: obj(), annotations: ro },
  { name: 'hb_probe_points', description: 'Cast az/el or world points onto the scalp and return the surface positions and normals, without changing anything.',
    inputSchema: obj({ points: { type: 'array', items: point, minItems: 1, maxItems: 500 } }, ['points']), annotations: ro },
  { name: 'hb_list_groups', description: 'List all groups with names and counts.', inputSchema: obj(), annotations: ro },
  { name: 'hb_get_group', description: 'Full detail of one group: median card params, variance, shape curves, sidedness, guides and clumpers (with ids and az/el).',
    inputSchema: obj({ group_id: groupId }), annotations: ro },

  { name: 'hb_load_model', description: 'Load a head OBJ to groom (dialog-free). Refuses if a groom exists unless discard_groom=true.',
    inputSchema: obj({ path: str('Absolute path to an .obj file.'), albedo_path: str('Optional absolute path to a PNG/JPEG albedo for the head.'), discard_groom: bool('Discard the current groom.') }, ['path']),
    annotations: destructive, timeout: 180000 },
  { name: 'hb_load_project', description: 'Open a HairBrush project .json (dialog-free). Refuses if a groom exists unless discard_groom=true.',
    inputSchema: obj({ path: str('Absolute path to a .json project.'), discard_groom: bool('Discard the current groom.') }, ['path']),
    annotations: destructive, timeout: 180000 },
  { name: 'hb_save_project', description: 'Save the session as a HairBrush project .json.',
    inputSchema: obj({ path: str('Absolute path ending in .json.'), overwrite: bool('Replace an existing file.') }, ['path']), annotations: rw, timeout: 120000 },
  { name: 'hb_export_obj', description: 'Export the evaluated hair cards as OBJ (one object per group), in the source head\'s space.',
    inputSchema: obj({ path: str('Absolute path ending in .obj.'), overwrite: bool('Replace an existing file.') }, ['path']), annotations: rw, timeout: 180000 },

  { name: 'hb_create_group', description: 'Create a new group (like + GROUP) and select it. Returns its id.',
    inputSchema: obj({ name: str('Group name, e.g. "Fringe", "Crown", "Sides".') }), annotations: rw },
  { name: 'hb_select_group', description: 'Select a group in the UI (sliders then show its values).',
    inputSchema: obj({ group_id: groupId }, ['group_id']), annotations: rw },
  { name: 'hb_rename_group', description: 'Rename a group.',
    inputSchema: obj({ group_id: groupId, name: str('New name.') }, ['group_id', 'name']), annotations: rw },
  { name: 'hb_delete_group', description: 'Delete a group and all its cards.',
    inputSchema: obj({ group_id: groupId }, ['group_id']), annotations: destructive },
  { name: 'hb_set_group_flags', description: 'Set per-group render flags.',
    inputSchema: obj({ group_id: groupId, single_sided: bool('Render cards single-sided.'), flip_normals: bool('Flip card normals.') }), annotations: rw },

  { name: 'hb_place_cards', description: 'Place hair cards at specific scalp points (like clicking). Mirrors across the symmetry plane when symmetry is on (unless mirror=false). Optional params set the placement defaults for these new cards only.',
    inputSchema: obj({ group_id: groupId, points: { type: 'array', items: point, minItems: 1, maxItems: 5000 }, params: cardParams, mirror: bool('Honour symmetry (default true).') }, ['points']),
    annotations: rw, timeout: 120000 },
  { name: 'hb_fill_region', description: 'Evenly cover an az/el region of the scalp with cards at the given spacing (like the EVEN brush over a whole area). Avoids existing cards of the group and symmetry mirrors. With symmetry on, fill one side only (e.g. az 0..180) and let the mirror do the other.',
    inputSchema: obj({
      group_id: groupId, ...regionProps,
      exclude: { type: 'array', items: obj(regionProps), description: 'Sub-regions to skip (e.g. around the ears).' },
      spacing: num('Minimum distance between card roots in world units (default 0.01; 0.004-0.008 for dense hair).', { minimum: 0.002 }),
      max_cards: int('Upper limit on primary cards placed (default 3000).', { minimum: 1, maximum: 30000 }),
      seed: int('Random seed for the scatter.'), mirror: bool('Honour symmetry (default true).'),
      avoid_existing: bool('Keep spacing from cards already in the group (default true).'), params: cardParams,
    }), annotations: rw, timeout: 300000 },
  { name: 'hb_erase_cards', description: 'Erase cards of a group: all of them, or those whose roots lie within radius of a point (mirrored when symmetry is on).',
    inputSchema: obj({ group_id: groupId, all: bool('Erase every card in the group.'), point, radius: num('Erase radius in world units (default 0.03).'), mirror: bool('Also erase at the mirrored point (default true).') }),
    annotations: destructive },
  { name: 'hb_set_group_params', description: 'Set card parameters for every card in a group (exactly like moving the grooming sliders with the group selected). Values are absolute.',
    inputSchema: obj({ group_id: groupId, params: cardParams }, ['params']), annotations: rw, timeout: 120000 },
  { name: 'hb_set_variance', description: 'Per-card random variation for a group: each card gets base +/- a random share of amount, in the channel\'s own units (Length/Width/CurlDiameter/WaveAmplitude: world units, e.g. Length 0.01 = +/-1cm; Bend/Twist/AngleX/Y/Z: degrees, e.g. AngleY 15). amount 0 turns a channel off.',
    inputSchema: obj({ group_id: groupId, channels: { type: 'array', minItems: 1, items: obj({ channel: str('Channel.', { enum: VARIANCE_CHANNELS }), amount: num('Variation in the channel\'s units (see description).', { minimum: 0 }), seed: int('Seed.') }, ['channel']) } }, ['channels']),
    annotations: rw, timeout: 120000 },
  { name: 'hb_set_shape_curve', description: 'Set a group\'s shape curve: a multiplier profile along the hair from root (t=0) to tip (t=1), e.g. Width [[0,1],[1,0.3]] tapers the tips, Bend [[0,0],[1,1]] concentrates bend toward the tip.',
    inputSchema: obj({ group_id: groupId, channel: str('Channel.', { enum: SHAPE_CHANNELS }), keys: { type: 'array', items: { type: 'array', items: { type: 'number' }, minItems: 2, maxItems: 2 }, minItems: 2, description: '[[t, value], ...]' }, reset: bool('Reset the channel to default instead.') }, ['channel']),
    annotations: rw },

  { name: 'hb_add_guide', description: 'Add a GUIDE that combs nearby cards of the group along its curve. Root at az/el or position; shape from nodes_world, or from direction + length (e.g. direction "back", length 0.25 sweeps hair backwards).',
    inputSchema: obj({ group_id: groupId, ...point.properties, ...guideShape }), annotations: rw },
  { name: 'hb_edit_guide', description: 'Change a guide: move its root (az/el/position), reshape it, or change amount/radius/falloff/spin.',
    inputSchema: obj({ guide_id: int('Guide id.'), ...point.properties, ...guideShape }, ['guide_id']), annotations: rw },
  { name: 'hb_remove_guide', description: 'Remove a guide.', inputSchema: obj({ guide_id: int('Guide id.') }, ['guide_id']), annotations: destructive },
  { name: 'hb_add_clumper', description: 'Add a CLUMPER that gathers cards of the group into clumps around a scalp point.',
    inputSchema: obj({ group_id: groupId, ...point.properties, ...clumperProps }), annotations: rw },
  { name: 'hb_edit_clumper', description: 'Change a clumper.', inputSchema: obj({ clumper_id: int('Clumper id.'), ...point.properties, ...clumperProps }, ['clumper_id']), annotations: rw },
  { name: 'hb_remove_clumper', description: 'Remove a clumper.', inputSchema: obj({ clumper_id: int('Clumper id.') }, ['clumper_id']), annotations: destructive },

  { name: 'hb_set_hair_material', description: 'Set the global hair material: tint (multiplies the strand texture; white = texture as authored), smoothness, metallic, dither. Saved with the project.',
    inputSchema: obj({ tint: vec3('[r, g, b], 0-1 each. e.g. [0.32, 0.22, 0.15] for mid brown.'), smoothness: num('0-1', { minimum: 0, maximum: 1 }), metallic: num('0-1', { minimum: 0, maximum: 1 }), dither: num('0-1', { minimum: 0, maximum: 1 }) }),
    annotations: rw },
  { name: 'hb_get_uv_rects', description: 'List the UV rectangles (preset card strips) cut from the active hair material\'s atlas. Their ids are what predetermined UVs pick between.',
    inputSchema: obj(), annotations: ro },
  { name: 'hb_set_uv_rects', description: 'Define the UV rectangles on the hair atlas, or auto_detect them from the texture alpha (the texture panel\'s AUTO). Replaces the current set.',
    inputSchema: obj({ auto_detect: bool('Detect strips from the base-colour texture.'), rects: { type: 'array', items: obj({ id: int('Rect id (1+).'), u_min: num('0-1'), v_min: num('0-1'), u_max: num('0-1'), v_max: num('0-1'), flip_v: bool('Root at v_min instead of v_max.') }, ['u_min', 'v_min', 'u_max', 'v_max']) } }),
    annotations: destructive, timeout: 60000 },
  { name: 'hb_set_group_uv', description: 'Predetermined UVs for a group: each card is randomly (by seed) assigned one of the UV rects with id min_id..max_id. This is the intended way to texture cards - vary the strip per card instead of hand-setting u/v scale/offset.',
    inputSchema: obj({ group_id: groupId, predetermined: bool('Use predetermined rects (true) or the manual U/V sliders (false).'), min_id: int('Lowest rect id.', { minimum: 1 }), max_id: int('Highest rect id.', { minimum: 1 }), seed: int('Assignment seed.'), flip_v: bool('Flip every rect for this group.') }),
    annotations: rw },
  { name: 'hb_add_post', description: 'Add a POST (localized manipulator) to a group: a soft sphere on the scalp (radius + falloff) that offsets the group\'s card parameters inside it. RELATIVE (default) adds delta to each card; absolute=true overrides to baseline+delta. Use POSTs for regional shaping (lift at the front, shorter at the temples, longer at the crown) instead of extra groups.',
    inputSchema: obj({ group_id: groupId, ...point.properties, radius: num('Full-strength radius (world units).'), falloff: num('Blend distance beyond the radius.'), weight: num('0-1', { minimum: 0, maximum: 1 }), absolute: bool('Override instead of offset.'), label: str('Up to 6 characters.'), delta: deltaParams }),
    annotations: rw },
  { name: 'hb_edit_post', description: 'Change a POST: move it (az/el/position), resize, re-weight, or set delta channels (reset_delta clears them first).',
    inputSchema: obj({ post_id: int('POST id.'), ...point.properties, radius: num('Radius.'), falloff: num('Falloff.'), weight: num('0-1', { minimum: 0, maximum: 1 }), absolute: bool('Override instead of offset.'), label: str('Up to 6 characters.'), reset_delta: bool('Zero all deltas before applying delta.'), delta: deltaParams }, ['post_id']),
    annotations: rw },
  { name: 'hb_remove_post', description: 'Remove a POST.', inputSchema: obj({ post_id: int('POST id.') }, ['post_id']), annotations: destructive },
  { name: 'hb_set_symmetry', description: 'Turn left/right symmetry on or off. When on, placement and erasing mirror across the head\'s midline.',
    inputSchema: obj({ enabled: bool('Symmetry on/off.') }, ['enabled']), annotations: rw },
  { name: 'hb_set_view', description: 'Move the user\'s viewport camera to a preset or az/el view of the groom.',
    inputSchema: obj({ view: str('Preset view.', { enum: VIEWS.filter(v => v !== 'current') }), az: num('Camera azimuth (head frame).'), el: num('Camera elevation.'), zoom: num('Zoom factor (default 1, >1 closer).'), fit: str('Frame the whole groom or just the head.', { enum: ['groom', 'head'] }), target: vec3('World point to look at.') }),
    annotations: rw },
  { name: 'hb_screenshot', description: 'Render the groom and return an image, from a preset view, an az/el, or the user\'s current view. The user\'s camera is not moved. include_ui=true captures the actual app window instead (panels included).',
    inputSchema: obj({ view: str('Preset view (default three_quarter_right).', { enum: VIEWS }), az: num('Camera azimuth.'), el: num('Camera elevation.'), zoom: num('Zoom factor (default 1).'), fit: str('Frame the groom or just the head.', { enum: ['groom', 'head'] }), target: vec3('World point to look at.'), width: int('Pixels (64-2048, default 768).'), height: int('Pixels (64-2048, default 768).'), include_ui: bool('Capture the real window with UI.') }),
    annotations: ro, timeout: 60000 },
  { name: 'hb_undo', description: 'Undo the last step (same as Ctrl+Z in HairBrush).', inputSchema: obj(), annotations: destructive, timeout: 60000 },
  { name: 'hb_redo', description: 'Redo.', inputSchema: obj(), annotations: rw, timeout: 60000 },
];
