shader_type canvas_item;
render_mode unshaded;

uniform vec2 background_origin = vec2(-24.0);
uniform vec2 background_extent = vec2(1488.0, 858.0);
uniform float lamp_a = 1.0;
uniform float lamp_b = 1.0;

float patch(vec2 point, vec2 center, vec2 radius) {
	vec2 q = (point - center) / radius;
	return 1.0 - smoothstep(0.0, 1.0, dot(q, q));
}

void fragment() {
	vec4 paint = texture(TEXTURE, UV);
	vec2 point = background_origin + UV * background_extent;
	float glass_a = patch(point, vec2(638.0, 461.0), vec2(17.0, 29.0));
	float glass_b = patch(point, vec2(714.0, 592.0), vec2(11.0, 21.0));
	float wall_a = patch(point, vec2(628.0, 471.0), vec2(91.0, 125.0));
	float wall_b = patch(point, vec2(709.0, 593.0), vec2(54.0, 82.0));
	float water = patch(point, vec2(626.0, 744.0), vec2(86.0, 20.0));
	// Modulate the light already painted into the scene, including the glass.
	// An additive glow alone cannot visibly dim an already saturated lamp.
	float change_a = lamp_a - 1.0;
	float change_b = lamp_b - 1.0;
	float illumination = 1.0 + change_a * (glass_a * 0.85 + wall_a * 0.32 + water * 0.45)
		+ change_b * (glass_b * 0.85 + wall_b * 0.28);
	vec3 warm = vec3(1.0, 0.54, 0.18) * (max(change_a, 0.0) * wall_a + max(change_b, 0.0) * wall_b) * 0.18;
	COLOR = vec4(paint.rgb * max(0.15, illumination) + warm, paint.a);
}
