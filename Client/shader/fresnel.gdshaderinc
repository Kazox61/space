float fresnel(float power, vec3 normal, vec3 view) {
    return pow(1.0 - max(dot(normalize(normal), normalize(view)), 0.0), power);
}