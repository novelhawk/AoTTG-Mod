using System;
using System.Collections;

public class IComparerPVPchkPtID : IComparer
{
    int IComparer.Compare(object x, object y)
    {
        float id = ((PVPcheckPoint) x).id;
        float num2 = ((PVPcheckPoint) y).id;
        if (Equals(id, num2) || Math.Abs(id - num2) < float.Epsilon)
        {
            return 0;
        }
        if (id < num2)
        {
            return -1;
        }
        return 1;
    }
}

