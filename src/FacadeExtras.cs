namespace GEE;

/// <summary>
/// GLSL helpers for FacadeTechnique's fragment shader (spliced in after Hash): balconies on the upper floors of
/// housing walls, shop fronts (stall riser, glass, awning, fascia with lettering) on the ground floor of office and
/// ribbon-window walls.
///
/// The window pass blends out = src + dst * alpha, so geometry and shading can only darken the lit facade, and
/// colour can only be added. Awnings and fascias therefore darken (alpha) and add a small colour term (src) in
/// daylight, and at night they are emissive (the shader's existing emission path), which gives them real colour.
/// A second blend factor (dual-source blending) would allow true multiplicative tint but needs the dualSrcBlend
/// device feature and a change to the shared pipeline; the additive term at about 10 % is enough for a hue and
/// leaves the pipeline, and every other agent's shader edits, untouched.
///
/// BalconyPlan (C#) repeats the balcony decisions with the same hash, so real balcony pieces line up with the
/// painted ones: keep both in step.
/// </summary>
internal static class FacadeExtras
{
    /// <summary>Wall-level and bay-level odds, shared with BalconyPlan.</summary>
    public const float WallShare = 0.6f, BaySharePct = 0.33f;

    public const string Glsl = """

        // 1 inside [a, b] along x, antialiased with half-width w.
        float span(float x, float a, float b, float w)
        {
            return smoothstep(a - w, a + w, x) - smoothstep(b - w, b + w, x);
        }

        // Balconies: some bays of some walls carry a column of balconies from the first floor up. Returns how much
        // darker the pixel gets (slab edge, railing, shadow under the slab above). wp: metres along/above the wall,
        // pw: metres per pixel.
        float balconyDarkening(vec2 wp, vec2 pw, float bayW, float floorH, float floors, float width, uint seed, uint wall)
        {
            if (floors < 3.0 || width < 8.0 || wp.y <= 0.0)
                return 0.0;
            if (random(seed, wall, 12u) >= 0.6)
                return 0.0;
            float bay = floor(wp.x / bayW);
            uint key = uint(bay) + wall * 256u;
            if (random(seed, key, 11u) >= 0.33)
                return 0.0;
            float fl = floor(wp.y / floorH);
            if (fl >= floors)
                return 0.0;
            float xm = (wp.x - bay * bayW);
            float y = wp.y - fl * floorH;
            float inX = span(xm, 0.1 * bayW, 0.9 * bayW, pw.x);
            // Railing kind: balusters, solid panel or glass.
            float kind = random(seed, key, 13u);
            float railDark = kind < 0.75 ? 0.30 : 0.20;
            // Fade the fine detail out when a pixel is longer than the slab is thick.
            float detail = 1.0 - smoothstep(0.12, 0.4, pw.y);
            float balusters = 1.0;
            if (kind < 0.4)
            {
                float pitch = 0.14;
                float d = abs(fract(xm / pitch + 0.5) - 0.5) * pitch;
                float bar = 1.0 - smoothstep(0.015, 0.015 + pw.x, d);
                float barFade = smoothstep(0.03, 0.1, pw.x);
                balusters = mix(bar, 0.25, barFade);
            }
            // This floor's balcony (floors 1 and up): slab edge and railing above it.
            float own = 0.0;
            if (fl >= 1.0)
            {
                float slab = 1.0 - smoothstep(0.2 - pw.y, 0.2 + pw.y, y);
                float rail = span(y, 0.2, 1.0, pw.y) * balusters * railDark;
                float hand = span(y, 1.0, 1.06, pw.y) * 0.35;
                own = slab * 0.34 + rail + hand;
            }
            // Shadow the slab above throws onto the wall below it (floors 0 .. floors - 2).
            float shadow = fl + 1.0 < floors ? smoothstep(floorH - 0.55, floorH - 0.05, y) * 0.16 : 0.0;
            float mean = (fl >= 1.0 ? 0.075 : 0.0) + (fl + 1.0 < floors ? 0.02 : 0.0);
            float full = (own + shadow) * inX;
            return mix(mean * span(xm, 0.1 * bayW, 0.9 * bayW, bayW * 0.1), full, detail);
        }

        vec3 shopColour(uint i)
        {
            if (i == 0u) return vec3(0.80, 0.10, 0.10);
            if (i == 1u) return vec3(0.10, 0.45, 0.20);
            if (i == 2u) return vec3(0.12, 0.25, 0.70);
            if (i == 3u) return vec3(0.90, 0.65, 0.10);
            if (i == 4u) return vec3(0.05, 0.55, 0.60);
            if (i == 5u) return vec3(0.50, 0.10, 0.25);
            if (i == 6u) return vec3(0.85, 0.85, 0.80);
            return vec3(0.12, 0.12, 0.14);
        }

        // Ground-floor shop fronts: units of about 5.5 m along the wall, each with a stall riser, shop glass (a door at
        // one end), an optional striped awning and a coloured fascia with lettering. wp.y is metres above the ground,
        // floorH the ground floor's height. Overwrites alpha inside shop units; glow (night) and tint (day) are added.
        void shopFront(vec2 wp, vec2 pw, float floorH, float width, uint seed, uint wall, float foot,
                       inout float alpha, inout vec3 glow, inout vec3 tint)
        {
            if (width < 6.0 || wp.y <= 0.0 || wp.y >= floorH || floorH < 3.2)
                return;
            if (random(seed, wall, 21u) >= 0.65)
                return;
            float n = max(floor(width / 5.5), 1.0);
            float uw = width / n;
            float ux = wp.x / uw;
            float ui = floor(ux);
            float xm = (ux - ui) * uw;
            uint uid = uint(ui) + wall * 64u;
            if (random(seed, uid, 22u) < 0.12)
                return;                                   // a blank unit or entrance lobby keeps the plain windows
            vec3 col = shopColour(uint(random(seed, uid, 23u) * 8.0));
            bool awning = random(seed, uid, 24u) < 0.55;
            float y = wp.y;
            float yFascia0 = floorH - 1.05, yFascia1 = floorH - 0.3;
            float yAwn0 = floorH - 1.75, yAwn1 = yFascia0;
            // Piers between the units.
            float pier = 1.0 - span(xm, 0.3, uw - 0.3, pw.x);
            // Glass: from a 0.4 m stall riser up to the fascia, a door at the far end.
            float glassY = span(y, 0.4, yFascia0, pw.y);
            float glass = (1.0 - pier) * glassY;
            float doorX = span(xm, uw - 1.5, uw - 0.5, pw.x);
            float door = doorX * (1.0 - smoothstep(2.2 - pw.y, 2.2 + pw.y, y));
            float mull = (1.0 - smoothstep(0.02, 0.02 + pw.x, abs(xm - (uw - 1.7) * 0.5))) * glassY * (1.0 - pier);
            float a = 0.84;                                      // plain wall
            a = mix(a, 0.80, 1.0 - step(0.4, y));                // stall riser
            a = mix(a, 0.40, glass);
            a = mix(a, 0.34, door * glass);
            a -= 0.2 * mull;
            // Shadow of the awning (or of the fascia) over the upper part of the glass.
            a -= 0.10 * glass * (awning ? span(y, yAwn0 - 0.7, yAwn0, pw.y) : span(y, yFascia0 - 0.5, yFascia0, pw.y));
            float awnMask = 0.0, awnStripe = 0.0;
            if (awning)
            {
                float inX = span(xm, 0.15, uw - 0.15, pw.x);
                awnMask = inX * span(y, yAwn0, yAwn1, pw.y);
                float stripe = smoothstep(0.35, 0.65, abs(fract(xm / 0.7) * 2.0 - 1.0));
                awnStripe = mix(stripe, 0.5, smoothstep(0.08, 0.2, pw.x));
                a = mix(a, mix(0.50, 0.74, awnStripe), awnMask);
            }
            // Fascia sign: dark colour band, lettering lighter (cells of 0.3 m, a share of them filled).
            float fasciaMask = span(xm, 0.1, uw - 0.1, pw.x) * span(y, yFascia0, yFascia1, pw.y);
            float letters = 0.0;
            {
                float lx = span(xm, 0.5, uw - 0.5, pw.x) * span(y, yFascia0 + 0.18, yFascia1 - 0.16, pw.y);
                float cellId = floor(xm / 0.3);
                float on = random(seed, uint(cellId) + uid * 64u, 25u) < 0.6 ? 1.0 : 0.0;
                float gap = span(fract(xm / 0.3), 0.15, 0.85, pw.x / 0.3);
                letters = lx * mix(on * gap, 0.5, smoothstep(0.05, 0.15, pw.x));
            }
            a = mix(a, 0.52, fasciaMask);
            a = mix(a, 0.80, fasciaMask * letters);
            a -= 0.16 * foot * (1.0 - pier);
            alpha = clamp(a, 0.2, 1.0);
            // Day: a small colour term on the awning stripes and the fascia. Night: lit sign, lit shop interior.
            tint += col * 0.10 * (fasciaMask * (1.0 - 0.6 * letters) + awnMask * (1.0 - awnStripe));
            bool interiorLit = random(seed, uid, 26u) < 0.7;
            glow += col * 0.9 * fasciaMask * (0.3 + 0.7 * letters);
            glow += vec3(1.0, 0.85, 0.6) * 0.7 * glass * (1.0 - door) * (interiorLit ? 1.0 : 0.0) * (1.0 - awnMask);
        }

        """;
}
