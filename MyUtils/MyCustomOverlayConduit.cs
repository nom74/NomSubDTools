using Rhino.Geometry;
using System.Collections.Generic;
using System.Drawing;

namespace NomSubDTools.MyUtils
{
    class MyCustomOverlayConduit : Rhino.Display.DisplayConduit
    {
        private List<Point3d> m_lsPoint3D;
        private Color m_color;

        public MyCustomOverlayConduit(List<Point3d> lsPoint3D, Color color)
        {
            m_lsPoint3D = lsPoint3D;
            m_color = color;
        }



        public void UpdateVertices(List<Point3d> newVertices)
        {
            m_lsPoint3D = newVertices;
        }



        protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
        {
            for (int i = 0; i < m_lsPoint3D.Count; i++)
            {
                Point3d pt3dTextPosition = m_lsPoint3D[i];
                string strText = i.ToString();

                e.Display.Draw2dText(strText, m_color, pt3dTextPosition, false, 26);
            }
        }

    }




}
