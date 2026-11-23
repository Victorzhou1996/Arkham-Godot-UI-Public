shader_type canvas_item;
render_mode unshaded;

uniform sampler2D ownership : filter_nearest;
uniform float actor_index = 0.0;
uniform float emphasis = 1.0;
uniform float hover_amount = 0.0;
uniform float cinematic_amount = 0.0;

void fragment() {
	vec4 ink = texture(TEXTURE, UV);
	float owner = floor(texture(ownership, UV).r * 255.0 + 0.5);
	if (abs(owner - actor_index) > 0.1) { discard; }
	float brightness = mix(0.76, 1.04, emphasis) + hover_amount * 0.07;
	ink.rgb *= brightness;
	float luminance = dot(ink.rgb, vec3(0.2126, 0.7152, 0.0722));
	vec3 film_color = mix(vec3(luminance), ink.rgb, 0.78);
	film_color *= mix(vec3(0.62, 0.77, 0.84), vec3(1.07, 0.98, 0.85), smoothstep(0.08, 0.48, luminance));
	film_color *= mix(0.18, 1.0, emphasis);
	ink.rgb = mix(ink.rgb, film_color, cinematic_amount);
	COLOR = ink;
}
