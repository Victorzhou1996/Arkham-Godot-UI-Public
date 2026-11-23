shader_type canvas_item;
render_mode unshaded;
uniform sampler2D portrait_source : source_color, filter_linear, repeat_disable;
uniform vec4 portrait_region = vec4(0.0, 0.0, 1.0, 1.0);
uniform bool exhausted = false;
varying vec2 portrait_uv;

void vertex() {
	// Crop and circular clipping share normalized local pin coordinates.
	portrait_uv = VERTEX / vec2(52.0);
}

void fragment() {
	vec4 portrait = texture(portrait_source, portrait_region.xy + portrait_uv * portrait_region.zw);
	if (exhausted) {
		float grey = dot(portrait.rgb, vec3(0.299, 0.587, 0.114));
		portrait.rgb = mix(portrait.rgb, vec3(grey), 0.55) * 0.85;
	}
	float radius = length(portrait_uv - vec2(0.5));
	portrait.a *= 1.0 - smoothstep(0.475, 0.5, radius);
	COLOR = portrait;
}
