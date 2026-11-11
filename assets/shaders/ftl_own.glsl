// The pilot's own rift or wormhole seen from inside its jump, worked out from the frame constants alone. The
// composite pass reads it to show the world or the throat at a pixel; the hull's mesh pass reads it to show or hide
// each point of the pilot's own hull, so the two agree on the same opening, the same rays and the same dissolve.
// FtlAperture in Prospect.Game mirrors this file line for line, for picking; change both together.
// Needs frame.glsl and ftl.glsl.
#ifndef FTL_OWN_GLSL
#define FTL_OWN_GLSL

const float Far = 1e30;

// How far along the view's forward axis a point t along the view ray v is.
float rayDepth(vec3 v, float t)
{
    return t * dot(v, frame.cameraForward.xyz);
}

// Where a view ray first comes to a sphere: the near side of it, or where the ray passes closest when it misses.
float sphereRay(vec3 v, vec3 c, float r)
{
    float b = dot(c, v);
    float h = r * r - (dot(c, c) - b * b);
    return h > 0.0 ? b - sqrt(h) : b;
}

// What one view ray meets of the pilot's own opening.
struct FtlOwnRay
{
    // How much of what lies past the opening along the ray is the far side, 0 to 1.
    float far;
    // Where the ray meets the opening, along the view; Far where it does not.
    float at;
    // How many pixels inside the opening's outline the ray is; -1e4 with no outline.
    float edge;
    // How much a wormhole's rim line shows.
    float line;
    // A rift: where the ray meets its plane, from the centre, and that point across the window's half extents.
    vec3 hit;
    vec2 across;
    vec2 extent;
};

// While the drive works there is one opening, ahead: the real world on the near side, the throat beyond. After the
// drive has fired there is the one the hull comes out of: the throat on the near side, the destination beyond.
// Which side a ray shows follows from which side of the opening the camera is on (frame.ftlUp.w, eased) and what
// the ray passes through. A wormhole's opening is a sphere; inside the sphere the far side is a half of the view. A
// rift's is a flat window across the axis with a torn outline.
FtlOwnRay ftlOwnRay(vec3 v, float pixelAngle)
{
    int kind = int(frame.ftl.x + 0.5);
    bool entry = int(frame.ftl.y + 0.5) == 1;
    vec4 mouth = entry ? frame.ftlEntry : frame.ftlExit;
    float open = entry ? frame.ftlShape.y : frame.ftlShape.z;
    vec3 axis = frame.ftlAxis.xyz;
    vec3 up = frame.ftlUp.xyz;
    float seed = frame.ftl.w + (entry ? 0.0 : 0.37);
    vec3 c = mouth.xyz;
    float r = mouth.w;
    float past = frame.ftlUp.w;

    FtlOwnRay ray;
    ray.edge = -1e4;
    ray.line = 1.0;
    ray.at = Far;
    ray.hit = vec3(0.0);
    ray.across = vec2(0.0);
    ray.extent = vec2(0.0);
    if (kind == 3)
    {
        float facing = dot(v, axis);
        float t = abs(facing) > 1e-5 ? dot(c, axis) / facing : -1.0;
        if (r > 0.0 && t > 0.0)
        {
            ray.at = rayDepth(v, t);
            ray.hit = v * t - c;
            vec3 side = cross(up, axis);
            ray.extent = ftlRiftExtent(open) * r;
            ray.across = vec2(dot(ray.hit, side), dot(ray.hit, up)) / ray.extent;
            float re = length(ray.across);
            vec2 dir = ray.across / max(re, 1e-6);
            float perPixel = t * pixelAngle / max(abs(facing), 0.08);
            ray.edge = (ftlRiftOutline(atan(ray.across.y, ray.across.x), seed) - re) * length(dir * ray.extent) / perPixel;
        }
        float through = clamp(ray.edge + 0.5, 0.0, 1.0);
        ray.far = mix(through, 1.0 - through, past);
    }
    else
    {
        float d = length(c);
        if (r <= 0.0)
        {
            ray.far = past;
        }
        else if (d > r)
        {
            // The rim fades as the camera reaches the sphere, where it would become a line straight across the view.
            ray.line = smoothstep(r, r * 1.4, d);
            float t = sphereRay(v, c, r);
            ray.at = t > 0.0 ? rayDepth(v, t) : Far;
            float rim = asin(r / d);
            ray.edge = (rim - acos(clamp(dot(v, c / d), -1.0, 1.0))) / pixelAngle;
            float hit = clamp(ray.edge + 0.5, 0.0, 1.0);
            ray.far = mix(hit, 1.0 - hit, past);
        }
        else
        {
            // Inside, the far side is a half of the view. Just inside the surface it is the half facing the centre
            // (or away from it, once past), as it is just outside; deep inside it is the half ahead along the axis.
            // The camera can come in anywhere on the surface without the boundary jumping.
            vec3 toward = d > 1e-4 ? c / d : axis;
            float w = smoothstep(0.5 * r, r, d);
            vec3 n = axis * (1.0 - w) + toward * ((1.0 - 2.0 * past) * w);
            n = dot(n, n) > 1e-6 ? normalize(n) : axis;
            ray.edge = asin(clamp(dot(v, n), -1.0, 1.0)) / pixelAngle;
            ray.far = clamp(ray.edge + 0.5, 0.0, 1.0);
            ray.line = 0.0;
        }
    }
    return ray;
}

// How much of the real world shows along view ray v up to a surface at view depth `depth`: something nearer than
// where the ray meets the opening is on the camera's own side.
float ftlOwnReal(FtlOwnRay ray, float depth)
{
    bool entry = int(frame.ftl.y + 0.5) == 1;
    float far = depth < ray.at ? frame.ftlUp.w : ray.far;
    return entry ? 1.0 - far : far;
}

// How much of the opening's window lies between the camera and a surface at view depth `depth` along the ray:
// none where the ray meets the opening beyond the surface, or misses it.
float ftlOwnWindow(FtlOwnRay ray, float depth)
{
    return depth < ray.at ? 0.0 : clamp(ray.edge + 0.5, 0.0, 1.0);
}

// How much of a point of the pilot's own hull shows, p relative to the camera. Where the point is follows the hull,
// not the view: it is in the throat past the entry and before the exit, and in the world otherwise.
// - In the world, it is there to be seen, as anything in the world is, unless the opening's window is in front of
//   it while the camera is on the world's side: the window then shows the throat, as it does over a rock behind it.
//   So a stern not yet in stays in view from every side, beside and in front of the opening, however the view
//   dissolves; seen through the window from the throat's side, it shows.
// - In the throat, it shows only as far as the camera is in the throat, and not through the window, which shows the
//   world from there. From the world nothing past the opening is seen, as for every other hull, so the hull goes in
//   nose first and comes out with its stern last; as the camera's side eases into the throat, so does this.
float ftlOwnHullShows(vec3 p, float pixelAngle)
{
    bool entry = int(frame.ftl.y + 0.5) == 1;
    vec3 c = entry ? frame.ftlEntry.xyz : frame.ftlExit.xyz;
    float past = frame.ftlUp.w;
    float distance = length(p);
    vec3 v = distance > 1e-6 ? p / distance : frame.cameraForward.xyz;
    float window = ftlOwnWindow(ftlOwnRay(v, pixelAngle), rayDepth(v, distance));
    float worldView = entry ? 1.0 - past : past;
    float throatSide = dot(p - c, frame.ftlAxis.xyz) * (entry ? 1.0 : -1.0);
    return throatSide > 0.0 ? (1.0 - window) * (1.0 - worldView) : 1.0 - window * worldView;
}
#endif
