namespace Tests.CSharp
{
    /// <summary>C# <c>dynamic</c>'s own COM event wiring, the baseline <c>Tests/Com.fs</c> compares with.</summary>
    public static class CSharpComEvents
    {
        /// <summary>
        /// Subscribes <c>MoveComplete</c> on an <c>ADODB.Recordset</c> with <c>+=</c>, moves, unsubscribes
        /// with <c>-=</c>, moves again, and returns how many events the handler saw.
        /// </summary>
        public static int MovesSeen(object recordset)
        {
            dynamic rs = recordset;
            var seen = 0;
            System.Action<object, object, object, object> handler = (reason, error, status, source) => seen++;
            rs.MoveComplete += handler;
            rs.MoveFirst();
            rs.MoveComplete -= handler;
            rs.MoveLast();
            return seen;
        }

        /// <summary>C# <c>dynamic</c>'s own COM out: ADO's <c>Connection.Execute(sql, out object n)</c>'s RecordsAffected.</summary>
        public static object RecordsAffected(object connection, string sql)
        {
            dynamic conn = connection;
            conn.Execute(sql, out object affected);
            return affected;
        }
    }
}
