using System;
using System.Windows.Media.Animation;

namespace ClickSyncMouseTester.Infrastructure;

public class CubicBezierEase : IEasingFunction
{
    private const int NewtonIterations = 8;

    private const int BinarySubdivisionIterations = 10;

    private const double Precision = 1E-06;

    public double X1 { get; set; }

    public double Y1 { get; set; }

    public double X2 { get; set; }

    public double Y2 { get; set; }

    public double Ease(double normalizedTime)
    {
        if (normalizedTime <= 0.0)
        {
            return 0.0;
        }
        if (normalizedTime >= 1.0)
        {
            return 1.0;
        }

        double curveParameter = SolveCurveX(normalizedTime);
        return SampleCurveY(curveParameter);
    }

    double IEasingFunction.Ease(double normalizedTime)
    {
        return Ease(normalizedTime);
    }

    private double SolveCurveX(double targetX)
    {
        double parameter = targetX;
        for (int iteration = 0; iteration < NewtonIterations; iteration++)
        {
            double xError = SampleCurveX(parameter) - targetX;
            if (Math.Abs(xError) < Precision)
            {
                return parameter;
            }

            double derivative = SampleCurveDerivativeX(parameter);
            if (Math.Abs(derivative) < Precision)
            {
                break;
            }

            parameter -= xError / derivative;
        }

        double lowerBound = 0.0;
        double upperBound = 1.0;
        parameter = targetX;
        for (int iteration = 0; iteration < BinarySubdivisionIterations; iteration++)
        {
            double sampledX = SampleCurveX(parameter);
            if (Math.Abs(sampledX - targetX) < Precision)
            {
                break;
            }

            if (sampledX < targetX)
            {
                lowerBound = parameter;
            }
            else
            {
                upperBound = parameter;
            }
            parameter = (lowerBound + upperBound) * 0.5;
        }
        return parameter;
    }

    private double SampleCurveX(double parameter)
    {
        return ((A(X1, X2) * parameter + B(X1, X2)) * parameter + C(X1)) * parameter;
    }

    private double SampleCurveY(double parameter)
    {
        return ((A(Y1, Y2) * parameter + B(Y1, Y2)) * parameter + C(Y1)) * parameter;
    }

    private double SampleCurveDerivativeX(double parameter)
    {
        return (3.0 * A(X1, X2) * parameter + 2.0 * B(X1, X2)) * parameter + C(X1);
    }

    private static double A(double first, double second)
    {
        return 1.0 - 3.0 * second + 3.0 * first;
    }

    private static double B(double first, double second)
    {
        return 3.0 * second - 6.0 * first;
    }

    private static double C(double first)
    {
        return 3.0 * first;
    }
}
