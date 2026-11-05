// Intended Unity fixture source. Not recovered output, and not compiled in this tree.
namespace RecoveryFixtures
{
    public class ArithmeticFixture
    {
        public int score;

        public static int Return42()
        {
            return 42;
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }

        public static int Sub(int a, int b)
        {
            return a - b;
        }

        public static int Mul(int a, int b)
        {
            return a * b;
        }

        public static int And(int a, int b)
        {
            return a & b;
        }

        public static int Orr(int a, int b)
        {
            return a | b;
        }

        public static int Eor(int a, int b)
        {
            return a ^ b;
        }

        public static int Max(int a, int b)
        {
            if (a < b)
                return b;
            return a;
        }

        public static bool Below(uint a, uint b)
        {
            if (a < b)
                return true;
            return false;
        }

        public static int SumTo(int n)
        {
            int sum = 0;
            int i = 0;
            while (i < n)
            {
                sum = sum + i;
                i = i + 1;
            }
            return sum;
        }

        public int GetScore()
        {
            return score;
        }

        public void SetScore(int value)
        {
            score = value;
        }
    }
}
