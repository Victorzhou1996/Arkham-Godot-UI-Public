shader_type canvas_item;
uniform float motion_progress : hint_range(0.0, 1.0) = 0.0;
uniform vec2 image_extent = vec2(1672.0, 941.0);
varying vec4 picture_tint;

void vertex() {
	picture_tint = COLOR;
}

float soft_ellipse(vec2 point, vec2 center, vec2 radius) {
	return 1.0 - smoothstep(0.65, 1.0, length((point - center) / radius));
}

void fragment() {
	vec2 sample_uv = UV;
	if (motion_progress > 0.0 && motion_progress < 1.0) {
		vec2 point = UV * image_extent;
		// Follow the existing silhouette on the wall, above and beside the door.
		float body = soft_ellipse(point, vec2(382.0, 392.0), vec2(49.0, 76.0));
		float shoulder = soft_ellipse(point, vec2(421.0, 355.0), vec2(66.0, 43.0));
		float head = soft_ellipse(point, vec2(467.0, 315.0), vec2(37.0, 47.0));
		float arm = soft_ellipse(point, vec2(494.0, 286.0), vec2(44.0, 27.0));
		float mask = max(max(body, shoulder), max(head, arm));
		// Keep photographic edges, bookcase and the straight door moulding fixed.
		mask *= smoothstep(350.0, 366.0, point.x);
		mask *= smoothstep(275.0, 288.0, point.y);
		float above_door = smoothstep(4.0, 18.0, 390.0 - (point.x - 400.0) * 0.17 - point.y);
		mask *= max(1.0 - smoothstep(383.0, 401.0, point.x), above_door);
		float height = clamp((453.0 - point.y) / 150.0, 0.0, 1.1);
		float envelope = sin(motion_progress * 3.14159265);
		// A single lean and recoil; both ends are exactly the original artwork.
		vec2 drift = vec2(8.0 * sin(motion_progress * 6.2831853) + 3.0 * envelope, -3.0 * envelope);
		sample_uv -= drift * envelope * height * mask / image_extent;
	}
	COLOR = texture(TEXTURE, sample_uv) * picture_tint;
}
