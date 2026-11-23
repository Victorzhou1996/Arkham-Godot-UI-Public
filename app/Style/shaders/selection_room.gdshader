shader_type canvas_item;
render_mode unshaded;

uniform float seconds = 0.0;
uniform float motion_amount = 1.0;
uniform float selected = 0.0;
uniform float attention = 0.0;
uniform sampler2D ambient_mask : filter_linear, repeat_disable;

float pool(vec2 p, vec2 center, vec2 radius) {
	vec2 q = (p - center) / radius;
	return exp(-dot(q, q) * 2.4);
}

void fragment() {
	vec2 p = UV;
	vec3 original = texture(TEXTURE, p).rgb;
	vec2 masks = texture(ambient_mask, UV).rg;
	// Glass-only mask excludes and erodes the wooden frame before any UV travel.
	float outside_energy = 0.78 + attention * 0.20 + (abs(selected - 4.0) < 0.1 ? 0.24 : 0.0);
	vec2 outside_drift = vec2(
		0.0038 * sin(seconds * 0.43 + UV.y * 8.0) + 0.0012 * sin(seconds * 0.77 - UV.y * 31.0),
		0.0032 * sin(seconds * 0.31 + UV.x * 12.0));
	p += outside_drift * masks.r * outside_energy * motion_amount;
	// The wall's impossible hand pivots from its lower shadow; architecture is masked out.
	float shadow_energy = 1.0 + attention * 0.18 + (abs(selected - 3.0) < 0.1 ? 0.38 : 0.0);
	float angle = sin(seconds * 0.51) * 0.016 * shadow_energy;
	vec2 shadow_anchor = vec2(0.185, 0.386);
	vec2 q = UV - shadow_anchor;
	vec2 turn = vec2(cos(angle) * q.x - sin(angle) * q.y, sin(angle) * q.x + cos(angle) * q.y);
	vec2 shadow_drift = turn - q;
	shadow_drift.x += 0.0020 * sin(seconds * 0.72 + UV.x * 29.0) * (1.0 - smoothstep(0.12, 0.34, UV.y));
	p += shadow_drift * masks.g * motion_amount;
	float fire = pool(p, vec2(0.065, 0.626), vec2(0.085, 0.16));
	float flame_mask = fire * smoothstep(0.29, 0.70, original.r) * smoothstep(1.05, 1.75, original.r / max(original.g, 0.02));
	p.x += motion_amount * flame_mask * 0.0022 * sin(p.y * 77.0 - seconds * 3.2);
	p.y += motion_amount * flame_mask * 0.0028 * sin(p.x * 97.0 + seconds * 2.7);
	vec3 color = texture(TEXTURE, p).rgb;
	color *= 1.0 - motion_amount * masks.r * (0.025 + 0.025 * sin(seconds * 0.36 + UV.y * 4.0));
	float fire_energy = sin(seconds * 3.1) * 0.036 + sin(seconds * 5.73 + 1.0) * 0.019;
	color *= 1.0 + motion_amount * fire * fire_energy;
	float hall = pool(UV, vec2(0.48, 0.47), vec2(0.095, 0.29));
	float breath = 0.5 + 0.5 * sin(seconds * 0.39);
	color *= 1.0 - motion_amount * hall * (0.07 + breath * 0.15);
	float lamp = pool(UV, vec2(0.752, 0.46), vec2(0.13, 0.19));
	color *= 1.0 + motion_amount * lamp * (sin(seconds * 1.37) * 0.016 + sin(seconds * 4.13) * 0.009);
	float window = pool(UV, vec2(0.902, 0.20), vec2(0.16, 0.29));
	float dark_ink = 1.0 - smoothstep(0.10, 0.24, dot(original, vec3(0.2126, 0.7152, 0.0722)));
	color *= 1.0 - motion_amount * window * dark_ink * (0.05 + 0.05 * sin(seconds * 0.24 + UV.y * 3.0));
	COLOR = vec4(color, 1.0);
}
