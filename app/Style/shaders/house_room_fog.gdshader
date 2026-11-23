shader_type canvas_item;
render_mode unshaded;

uniform float darkness = 0.52;
uniform vec2 source_texel = vec2(0.000598, 0.001063);
uniform sampler2D blurred_source : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 boundary[6];
uniform int vertex_count = 4;
uniform float room_seed = 0.0;
uniform bool motion_enabled = true;
uniform float reveal_amount = 0.0;
uniform vec4 room_bounds = vec4(0.0, 0.0, 1.0, 1.0);

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(cell), hash(cell + vec2(1.0, 0.0)), f.x),
		mix(hash(cell + vec2(0.0, 1.0)), hash(cell + vec2(1.0)), f.x), f.y);
}

void fragment() {
	float clock = motion_enabled ? TIME * 0.035 : 0.0;
	vec2 aspect = vec2(1.0, 0.5625);
	float edge_distance = 1.0;
	for (int i = 0; i < 6; i++) {
		if (i < vertex_count) {
			int next = (i + 1) % vertex_count;
			vec2 segment = (boundary[next] - boundary[i]) * aspect;
			vec2 point = (UV - boundary[i]) * aspect;
			float along = clamp(dot(point, segment) / max(dot(segment, segment), 0.000001), 0.0, 1.0);
			edge_distance = min(edge_distance, length(point - segment * along));
		}
	}
	vec2 drift = vec2(clock, -clock * 0.38);
	float cloud = noise(UV * vec2(10.0, 8.0) + drift + room_seed);
	float wisps = noise(UV * vec2(25.0, 18.0) - drift * 0.7 + cloud);
	float veil = cloud * 0.68 + wisps * 0.32;
	// Blur the actual painted room, rather than placing a dark fill over it.
	// Mip filtering removes fine detail without the repeated edges of sparse taps.
	vec2 spread = source_texel * (6.0 + veil * 3.0);
	float lod = 3.0 + veil * 0.6;
	vec3 blurred = textureLod(blurred_source, UV, lod).rgb * 0.4;
	blurred += textureLod(blurred_source, UV + vec2(spread.x, spread.y), lod).rgb * 0.15;
	blurred += textureLod(blurred_source, UV - vec2(spread.x, spread.y), lod).rgb * 0.15;
	blurred += textureLod(blurred_source, UV + vec2(spread.x, -spread.y), lod).rgb * 0.15;
	blurred += textureLod(blurred_source, UV + vec2(-spread.x, spread.y), lod).rgb * 0.15;
	float tone = dot(blurred, vec3(0.2126, 0.7152, 0.0722));
	// Slate shadows follow the study palette while retaining muted painted hues.
	vec3 silhouette = mix(vec3(tone) * vec3(0.78, 0.83, 0.96), blurred, 0.30);
	vec3 fog_color = mix(vec3(0.025, 0.030, 0.044), vec3(0.070, 0.075, 0.092), veil);
	vec3 obscured = fog_color + silhouette * darkness * mix(0.66, 1.05, veil);
	// Feather follows every polygon edge, including the attic's sloping rafters.
	float feather = smoothstep(0.0, 0.020 + cloud * 0.012, edge_distance);
	float from_floor = clamp((room_bounds.y + room_bounds.w - UV.y) / room_bounds.w, 0.0, 1.0);
	float retreat = 1.0 - smoothstep(from_floor * 0.65, from_floor * 0.65 + 0.35, reveal_amount);
	COLOR = vec4(obscured, feather * 0.97 * retreat);
}
