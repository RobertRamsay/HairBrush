using System.Collections.Generic;
using UnityEngine;

// The landmark warp: a 3D thin plate spline through the marker pairs.
//
// Why not an affine fit. Four markers and a similarity gets you a rigid transform, which is wrong
// everywhere the two heads differ in shape - and differing in shape is the entire point. A spline
// interpolates every marker pair exactly, varies smoothly between them, and is a single
// (N+4)x(N+4) linear solve that then applies to any number of anchors for free.
//
//     f(x) = a0 + a1*x + a2*y + a3*z + SUM_i w_i * U(|x - p_i|),   U(r) = r in three dimensions
//
// U(r) = r rather than the r^2*log(r) everyone remembers: that one is the two-dimensional kernel.
// The three-dimensional biharmonic fundamental solution is plain r, and using the 2D kernel in 3D
// gives a warp that looks plausible and interpolates wrongly.
//
// The four extra rows are the orthogonality conditions that stop the smooth part absorbing an
// affine transform the polynomial should be carrying.
//
// Regularised rather than exact. lambda on the diagonal turns exact interpolation into a smooth
// approximation, so one mis-placed marker pair bends the field instead of tearing it. A spline is
// not guaranteed injective, and a swapped pair produces a fold with no error raised - which is
// why RemapMarkerSet.TryFindSideMismatch runs before this ever gets called.
public class ThinPlateSpline3D
{
    private Vector3[] controls = new Vector3[0];
    private Vector3[] weights = new Vector3[0];
    // affine[0] is the constant term; affine[1..3] are the x, y and z columns.
    private Vector3[] affine = new Vector3[4];
    private bool valid;

    public bool Valid { get { return valid; } }
    public int ControlCount { get { return controls.Length; } }

    // A regularisation strength that means the same thing at any model scale.
    //
    // lambda goes on the diagonal of a matrix whose off-diagonal entries are distances between
    // control points, so an absolute lambda is only meaningful against a particular size of head.
    // At HairBrush's normalised 0.33 units a lambda that behaved gently on a 2-unit model is
    // nearly exact interpolation, and the mis-placed marker it was supposed to absorb tears the
    // field instead. Expressed as a fraction of the mean control separation, one number holds.
    public static float SuggestedLambda(List<Vector3> source, float fractionOfMeanSpacing)
    {
        if (source == null || source.Count < 2) return 0f;
        double total = 0.0;
        int pairs = 0;
        for (int i = 0; i < source.Count; i++)
        {
            for (int j = i + 1; j < source.Count; j++)
            {
                total += Distance(source[i], source[j]);
                pairs++;
            }
        }
        if (pairs == 0) return 0f;
        return (float)(total / pairs) * fractionOfMeanSpacing;
    }

    // lambda is in the same units as the control points - see SuggestedLambda, which is how
    // callers should arrive at it. Zero is exact interpolation.
    public static ThinPlateSpline3D Solve(List<Vector3> source, List<Vector3> target, float lambda)
    {
        ThinPlateSpline3D spline = new ThinPlateSpline3D();
        if (source == null || target == null) return spline;
        int n = source.Count;
        if (n != target.Count || n < 4) return spline;

        int size = n + 4;
        double[,] m = new double[size, size];
        double[,] rhs = new double[size, 3];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j)
                {
                    m[i, j] = lambda;
                    continue;
                }
                m[i, j] = Distance(source[i], source[j]);
            }

            m[i, n + 0] = 1.0;
            m[i, n + 1] = source[i].x;
            m[i, n + 2] = source[i].y;
            m[i, n + 3] = source[i].z;

            m[n + 0, i] = 1.0;
            m[n + 1, i] = source[i].x;
            m[n + 2, i] = source[i].y;
            m[n + 3, i] = source[i].z;

            rhs[i, 0] = target[i].x;
            rhs[i, 1] = target[i].y;
            rhs[i, 2] = target[i].z;
        }

        if (!SolveInPlace(m, rhs, size)) return spline;

        spline.controls = source.ToArray();
        spline.weights = new Vector3[n];
        for (int i = 0; i < n; i++) spline.weights[i] = new Vector3((float)rhs[i, 0], (float)rhs[i, 1], (float)rhs[i, 2]);
        for (int k = 0; k < 4; k++) spline.affine[k] = new Vector3((float)rhs[n + k, 0], (float)rhs[n + k, 1], (float)rhs[n + k, 2]);
        spline.valid = true;
        return spline;
    }

    public Vector3 Map(Vector3 p)
    {
        if (!valid) return p;
        Vector3 result = affine[0] + affine[1] * p.x + affine[2] * p.y + affine[3] * p.z;
        for (int i = 0; i < controls.Length; i++) result += weights[i] * Distance(p, controls[i]);
        return result;
    }

    // Rows of the Jacobian, one per output axis. The warp is nonlinear, so a direction cannot be
    // carried through Map: it has to go through the local derivative.
    public void Jacobian(Vector3 p, out Vector3 rowX, out Vector3 rowY, out Vector3 rowZ)
    {
        rowX = new Vector3(1f, 0f, 0f);
        rowY = new Vector3(0f, 1f, 0f);
        rowZ = new Vector3(0f, 0f, 1f);
        if (!valid) return;

        rowX = new Vector3(affine[1].x, affine[2].x, affine[3].x);
        rowY = new Vector3(affine[1].y, affine[2].y, affine[3].y);
        rowZ = new Vector3(affine[1].z, affine[2].z, affine[3].z);

        for (int i = 0; i < controls.Length; i++)
        {
            Vector3 d = p - controls[i];
            float r = Magnitude(d);
            // The kernel's gradient is undefined exactly at a control point. It is also
            // bounded either side, so stepping over the singularity loses nothing real.
            if (r < .000001f) continue;
            Vector3 g = d / r;
            rowX += new Vector3(weights[i].x * g.x, weights[i].x * g.y, weights[i].x * g.z);
            rowY += new Vector3(weights[i].y * g.x, weights[i].y * g.y, weights[i].y * g.z);
            rowZ += new Vector3(weights[i].z * g.x, weights[i].z * g.y, weights[i].z * g.z);
        }
    }

    // How much a world distance at this point is stretched. Every radius and falloff in the
    // project is a raw world distance compared against Vector3.Distance, so a warp that changes
    // scale without this silently changes the reach of every clumper, POST and guide.
    //
    // The cube root of the Jacobian determinant: the determinant is the VOLUME ratio, and a
    // length ratio is its cube root.
    public float LocalScale(Vector3 p)
    {
        if (!valid) return 1f;
        Vector3 rowX;
        Vector3 rowY;
        Vector3 rowZ;
        Jacobian(p, out rowX, out rowY, out rowZ);

        float det =
            rowX.x * (rowY.y * rowZ.z - rowY.z * rowZ.y) -
            rowX.y * (rowY.x * rowZ.z - rowY.z * rowZ.x) +
            rowX.z * (rowY.x * rowZ.y - rowY.y * rowZ.x);

        float magnitude = Mathf.Abs(det);
        // A determinant at or below zero is a fold - the warp has turned the neighbourhood inside
        // out. Reported as a scale of 1 rather than an imaginary number; the caller's job is to
        // have stopped a fold happening, not to render one prettily.
        if (magnitude < .000001f) return 1f;
        return Mathf.Pow(magnitude, 1f / 3f);
    }

    // A normal is a covector: it transforms by the inverse transpose, not by the Jacobian. Under
    // a shear - which is most of what a head-to-head warp is - carrying it the wrong way tilts
    // every normal off the surface it is supposed to be perpendicular to.
    //
    // Built here as the cross product of two mapped tangents, which IS the inverse transpose up to
    // a scale factor and avoids inverting a 3x3 that may be near-singular.
    public Vector3 MapNormal(Vector3 p, Vector3 n)
    {
        if (!valid) return n;
        Vector3 rowX;
        Vector3 rowY;
        Vector3 rowZ;
        Jacobian(p, out rowX, out rowY, out rowZ);

        Vector3 unit = n;
        if (unit.sqrMagnitude < .000001f) unit = Vector3.up;
        unit = unit.normalized;

        Vector3 tangent = Vector3.Cross(unit, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(unit, Vector3.right);
        tangent = tangent.normalized;
        Vector3 bitangent = Vector3.Cross(unit, tangent).normalized;

        Vector3 mappedTangent = Apply(rowX, rowY, rowZ, tangent);
        Vector3 mappedBitangent = Apply(rowX, rowY, rowZ, bitangent);
        Vector3 mapped = Vector3.Cross(mappedTangent, mappedBitangent);
        if (mapped.sqrMagnitude < .000001f) return unit;

        mapped = mapped.normalized;
        // The cross product of two mapped tangents can come out pointing inward if the local
        // frame was left-handed to begin with. Agreeing with the original is the tie-break.
        if (Vector3.Dot(mapped, unit) < 0f) mapped = -mapped;
        return mapped;
    }

    static Vector3 Apply(Vector3 rowX, Vector3 rowY, Vector3 rowZ, Vector3 v)
    {
        return new Vector3(Vector3.Dot(rowX, v), Vector3.Dot(rowY, v), Vector3.Dot(rowZ, v));
    }

    static float Distance(Vector3 a, Vector3 b)
    {
        return Magnitude(a - b);
    }

    static float Magnitude(Vector3 v)
    {
        return Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
    }

    // Gaussian elimination with partial pivoting, in double precision.
    //
    // Doubles rather than floats throughout the solve: the matrix mixes kernel distances against
    // the 1/x/y/z polynomial block, and at HairBrush's working scale - a head normalised to 0.33
    // units - those differ by enough orders of magnitude that single precision loses the affine
    // part. The result is cast back to float only once it is a coefficient.
    static bool SolveInPlace(double[,] m, double[,] rhs, int size)
    {
        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            double best = System.Math.Abs(m[column, column]);
            for (int row = column + 1; row < size; row++)
            {
                double candidate = System.Math.Abs(m[row, column]);
                if (candidate <= best) continue;
                best = candidate;
                pivot = row;
            }

            // Singular. Coincident markers and near-coplanar sets both land here, which is why
            // they are refused before the solve rather than diagnosed after it.
            if (best < 1e-12) return false;

            if (pivot != column)
            {
                for (int k = 0; k < size; k++)
                {
                    double swap = m[column, k];
                    m[column, k] = m[pivot, k];
                    m[pivot, k] = swap;
                }
                for (int k = 0; k < 3; k++)
                {
                    double swap = rhs[column, k];
                    rhs[column, k] = rhs[pivot, k];
                    rhs[pivot, k] = swap;
                }
            }

            double diagonal = m[column, column];
            for (int row = 0; row < size; row++)
            {
                if (row == column) continue;
                double factor = m[row, column] / diagonal;
                if (factor == 0.0) continue;
                for (int k = column; k < size; k++) m[row, k] -= factor * m[column, k];
                for (int k = 0; k < 3; k++) rhs[row, k] -= factor * rhs[column, k];
            }
        }

        for (int row = 0; row < size; row++)
        {
            double diagonal = m[row, row];
            for (int k = 0; k < 3; k++) rhs[row, k] /= diagonal;
        }
        return true;
    }
}
