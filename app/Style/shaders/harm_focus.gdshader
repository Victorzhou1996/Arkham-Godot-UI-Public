shader_type canvas_item;
// A single soft focus mask for the whole allocation, behind every target copy.
uniform vec4 targets[8];
uniform int target_count = 0;
uniform float strength = 0.0;
void fragment() {
    vec2 point = UV * vec2(1440.0, 810.0);
    float outside = 1.0;
    for (int i = 0; i < target_count; i++) {
        vec2 center = targets[i].xy + targets[i].zw * 0.5;
        vec2 distance_to_box = max(abs(point - center) - targets[i].zw * 0.5, vec2(0.0));
        outside = min(outside, smoothstep(0.0, 52.0, length(distance_to_box)));
    }
    COLOR = vec4(0.018, 0.023, 0.025, outside * strength * 0.43);
}
