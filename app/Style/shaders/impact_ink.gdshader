shader_type canvas_item;
render_mode unshaded;
uniform float progress : hint_range(0.0, 1.0) = 0.0;
uniform float horror = 0.0;
uniform float strength = 1.0;
float grain(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
void fragment() {
	vec2 p = (UV - vec2(0.5)) * vec2(1.0, 1.25);
	float r = length(p);
	float a = atan(p.y, p.x);
	float fade = smoothstep(0.0, 0.06, progress) * (1.0 - smoothstep(0.48, 1.0, progress));
	float edge = 1.0 - smoothstep(0.24, 0.55, r);
	float bloom = exp(-r * r * 14.0);
	float ring = exp(-pow((r - (0.05 + progress * 0.42)) * 29.0, 2.0));
	float wisps = 0.5 + 0.5 * sin(a * 4.0 + r * 29.0 - progress * 9.0);
	float ink = mix(bloom * 0.60 + ring * 0.5, bloom * 0.44 + ring * wisps * 0.45, horror);
	float texture_grain = 0.75 + grain(floor(UV * vec2(220.0, 180.0))) * 0.25;
	vec3 tint = mix(vec3(0.66, 0.19, 0.12), vec3(0.34, 0.27, 0.55), horror);
	COLOR = vec4(tint, clamp(ink * fade * edge * texture_grain * 0.40 * strength, 0.0, 0.34));
}
