using System;
//using ThreadSafeConsole;
using System.Net;
using System.Text;
using System.Linq;
using pageTelnet;

namespace stockTelnet
{
    class MainClass
    {
        public static void Main(string[] args)
        {
            ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("Hello World!"));
            ThreadSafeConsole.Console.flush();
            pageServer ss = new pageServer(IPAddress.Any, 1125);
            //ss.onClient = initPage;
            ss.onClient = initFathers;
            ss.start();
            ss.listenThread.Join();
            //socketHelper.scanSimple.term[] trms = socketHelper.scanSimple.scan(new socketHelper.scanSimple.term[] {new socketHelper.scanSimple.term(0xFF, false), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(0xFF, false)}, new byte[] {0xF0, 0xF4, 0xF1, 0xF2, 0xFF, 0xF0, 0xFF});
            //if (trms.Length == 0) Console.WriteLine("false");
            //if (trms.Length > 0) Console.WriteLine("true");
            //Console.WriteLine(socketHelper.scanSimple.getStart(new socketHelper.scanSimple.term[] { new socketHelper.scanSimple.term(0xFF, false), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(0xFF, false) }, new byte[] { 0xF0, 0xF4, 0xF1, 0xFF, 0xFF, 0xFF }));
        }

        public static void initPage(ServerUserObject state)
        {
            string greetText = " Welcome to the " + escapeCodeHelper.escapeCode + escapeCodeHelper.colorCyan + "MKSTK" + escapeCodeHelper.escapeCode + escapeCodeHelper.reset + " Exchange";
            string warnText = escapeCodeHelper.escapeCode + escapeCodeHelper.colorYellow + " Warning: Telnet is not secure, DO NOT send any sensitive data!" + escapeCodeHelper.escapeCode + escapeCodeHelper.reset;
            state.page = new serverPage(state.rTerminal);
            //state.page.controls.Add(new defaultControls.text(0, 0, state.rTerminal.width, 4, true));
            //state.page.controls.Add(new defaultControls.button(0, state.page.getNextY() + 1, "Disconnect"));
            state.page.controls.Add(new defaultControls.textInput(0, 0, 55, 4));
            ((defaultControls.textInput)state.page.controls[0]).value = "TEST";
            state.page.init();
            //((defaultControls.button)state.page.controls[1]).onClick =
            //    () => { state.rTerminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.clearScreenRestoreCursor + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft, false); state.workSocket.Shutdown(System.Net.Sockets.SocketShutdown.Both); state.workSocket.Close(); };
            //((defaultControls.text)state.page.controls[0]).addLine(greetText);
            //((defaultControls.text)state.page.controls[0]).addLine(warnText);
            state.page.render();
            state.rTerminal.onInput = state.page.onInput;
            //state.rTerminal.onResize = () => { ((defaultControls.text)state.page.controls[0]).width = state.rTerminal.width; ((defaultControls.text)state.page.controls[0]).height = state.rTerminal.height; state.page.render(); };
            state.rTerminal.onResize = state.page.render;
        }

        public static void initFathers(ServerUserObject state)
        {
            state.page = new serverPage(state.rTerminal);
            state.page.controls.Add(new FathersDay());
            state.page.init();
            state.page.render();
            state.rTerminal.onInput = (x) => { state.rTerminal.write(state.page.controls[0].render()); };
            state.rTerminal.onResize = () => { };
        }
    }

    public class FathersDay : pageControl
    {
        public string value { get; set; }
        public serverPage parent { get; set; }
        public int topLeftX { get; set; }
        public int topLeftY { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public bool input { get; set; }

        private int animationStep = 0;
        //private bool first = true;

        public FathersDay()
        {
            input = false;
            topLeftX = 0;
            topLeftY = 0;
            value = "";
        }

        public string onInput(byte[] ui)
        {
            return "";
        }

        public string render()
        {
            StringBuilder sb = new StringBuilder();

            double half = Math.Floor((double)parent.terminal.height / (double)2);
            //Reset the cursor
            sb.Append(escapeCodeHelper.escapeCode + "999" + escapeCodeHelper.cursorDown + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
            int y = animationStep;
            int x = (int)Math.Floor((double)parent.terminal.width / (double)2);

            if (animationStep <= parent.terminal.height)
            {
                //Play the launch animation
                //sb.Append(escapeCodeHelper.escapeCode + x + ";" + y + escapeCodeHelper.cursorPosition);
                sb.Append(escapeCodeHelper.escapeCode + x.ToString() + escapeCodeHelper.cursorRight + escapeCodeHelper.escapeCode + y.ToString() + escapeCodeHelper.cursorUp);
                sb.Append(" _ ");

                if (y > 1)
                {
                    //sb.Append(escapeCodeHelper.escapeCode + x + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "3" + escapeCodeHelper.cursorLeft);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append("/_\\");
                    //sb.Append(escapeCodeHelper.escapeCode + 1 + escapeCodeHelper.cursorUp);
                }
                if (y > 2)
                {
                    //sb.Append(escapeCodeHelper.escapeCode + x + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "3" + escapeCodeHelper.cursorLeft);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append("|_|");
                    //sb.Append(escapeCodeHelper.escapeCode + 1 + escapeCodeHelper.cursorUp);
                }
                if (y > 3)
                {
                    //sb.Append(escapeCodeHelper.escapeCode + x + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "3" + escapeCodeHelper.cursorLeft);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append("|_|");
                    //sb.Append(escapeCodeHelper.escapeCode + 1 + escapeCodeHelper.cursorUp);
                }
                if (y > 4)
                {
                    //sb.Append(escapeCodeHelper.escapeCode + x + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "3" + escapeCodeHelper.cursorLeft);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append("   ");
                    //sb.Append(escapeCodeHelper.escapeCode + 1 + escapeCodeHelper.cursorUp);
                }
            }
            else if (animationStep <= parent.terminal.height + half)
            {
                //Play explosion
                int cstep = (int)Math.Floor(parent.terminal.height + half) - animationStep;
                sb.Append(escapeCodeHelper.escapeCode + x.ToString() + escapeCodeHelper.cursorRight + escapeCodeHelper.escapeCode + y.ToString() + escapeCodeHelper.cursorUp);
                sb.Append(escapeCodeHelper.escapeCode + (4 + cstep).ToString() + escapeCodeHelper.cursorDown);
                sb.Append(escapeCodeHelper.escapeCode + (1 + cstep) + escapeCodeHelper.cursorLeft);
                int count = 1;
                sb.Append(new string((new string('*', 2*(cstep + 1)+3)).Select((c) => { count++; return (count % 2 == 0) ? c : ' '; }).ToArray()));
            }
            else
            {
                //Put the happy fathers day and mothers day text
                string t = "Love you Mom and Dad! Sorry for the late present!";
                string t2 = "Where does bad light end up? In a prism!";
                int cstep = animationStep - (int)Math.Floor(parent.terminal.height + half);
                if (cstep > t.Length) return "";
                sb.Append(escapeCodeHelper.escapeCode + (x - Math.Floor((double) half + (half / 2) + (half / 4))).ToString() + escapeCodeHelper.cursorRight + escapeCodeHelper.escapeCode + y.ToString() + escapeCodeHelper.cursorUp);
                sb.Append(escapeCodeHelper.escapeCode + (4).ToString() + escapeCodeHelper.cursorDown);
                sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.colorYellow + t.Substring(0, Math.Min(cstep, t.Length)) + escapeCodeHelper.escapeCode + escapeCodeHelper.reset);

                sb.Append(escapeCodeHelper.escapeCode + "999" + escapeCodeHelper.cursorDown + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                sb.Append(escapeCodeHelper.escapeCode + (x - Math.Floor((double)half + (half / 2) + (half / 4))).ToString() + escapeCodeHelper.cursorRight + escapeCodeHelper.escapeCode + y.ToString() + escapeCodeHelper.cursorUp);
                sb.Append(escapeCodeHelper.escapeCode + (5).ToString() + escapeCodeHelper.cursorDown);
                sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.colorYellow + t2.Substring(0, Math.Min(cstep, t2.Length)) + escapeCodeHelper.escapeCode + escapeCodeHelper.reset);
            }

            animationStep++;
            return sb.ToString();
        }

        public void clear()
        {
            
        }

        public pageControl clone()
        {
            return new FathersDay();
        }
    }
}
