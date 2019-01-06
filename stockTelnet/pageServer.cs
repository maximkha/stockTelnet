using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using ThreadSafeConsole;

namespace pageTelnet
{
    //ipconfig getifaddr `route -n get default | grep 'interface:' | grep -o '[^ ]*$'`
    //Gets internet interface
    //Better to use the gui interface
    public class pageServer
    {
        private Socket listener;
        private AutoResetEvent reset = new AutoResetEvent(false);
        private ManualResetEvent busyFlag = new ManualResetEvent(false);
        public Thread listenThread;
        public Action<ServerUserObject> onClient;
        public bool charmode = false;

        public pageServer(IPAddress listenInterface, int port)
        {
            IPEndPoint listenEndpoint = new IPEndPoint(listenInterface, port);
            listener = new Socket(listenEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(listenEndpoint);
            }
            catch (Exception ex)
            {
                ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine(ex.ToString()));
                ThreadSafeConsole.Console.flush();
                socketHelper.tryShutdown(listener);
            }
            listenThread = new Thread(startListen);
        }

        public void start()
        {
            listenThread.Start();
        }

        public void stop()
        {
            reset.Set();
            listenThread.Join();
        }

        private void startListen()
        {
            try
            {
                listener.Listen(100);
                while (true)
                {
                    if (reset.WaitOne(125)) //Maybe too fast?
                    {
                        socketHelper.tryShutdown(listener);
                        ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("Listener terminated"));
                        ThreadSafeConsole.Console.flush();
                        break;
                    }

                    busyFlag.Reset();
                    ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("Waiting for client..."));
                    ThreadSafeConsole.Console.flush();
                    listener.BeginAccept(new AsyncCallback(acceptClient), listener);
                    busyFlag.WaitOne();
                }
            }
            catch (ThreadAbortException)
            {
                socketHelper.tryShutdown(listener);
                ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("ThreadAbortException (THIS IS NOT A ERROR)"));
                ThreadSafeConsole.Console.flush();
            }
            catch (Exception ex)
            {
                ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine(ex.ToString()));
                ThreadSafeConsole.Console.flush();
                socketHelper.tryShutdown(listener);
            }
        }

        private void acceptClient(IAsyncResult result)
        {
            busyFlag.Set();
            //Socket listen = (Socket)result.AsyncState;
            Socket handler = listener.EndAccept(result);
            ServerUserObject state = new ServerUserObject();
            ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("New Client: " + handler.RemoteEndPoint));
            ThreadSafeConsole.Console.flush();
            state.workSocket = handler;

            state.rTerminal = new socketTerminal(handler);
            //https://stackoverflow.com/questions/273261/force-telnet-client-into-character-mode
            //http://www.pcmicro.com/netfoss/telnet.html
            //IAC WILL ECHO IAC WILL SUPPRESS_GO_AHEAD IAC WONT LINEMODE
            //255  251    1 255  251                 3 255  252       34

            //Tells Telnet client to set mode to character mode
            if (!charmode) state.rTerminal.writeBytes(0xFF, 0xFB, 0x01, 0xFF, 0xFB, 0x03, 0xFF, 0xFC, 0x22);

            //Tells Telnet client to send window size
            //IAC  DO   NAWS
            //255  253  31
            state.rTerminal.writeBytes(0xFF, 0xFD, 0x1F);

            onClient?.Invoke(state);

            handler.BeginReceive(state.buffer, 0, ServerUserObject.BufferSize, 0, new AsyncCallback(readCallback), state);
        }

        private void readCallback(IAsyncResult result)
        {
            ServerUserObject state = (ServerUserObject)result.AsyncState;
            int bytesRead = state.workSocket.EndReceive(result);  

            //If has IAC then process it
            //IAC can be escaped by another IAC
            if (bytesRead > 0) {
                //Handles the IAC messages
                if (state.buffer.All((x) => x == 0x00))
                {
                    ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("Empty buffer!"));
                    ThreadSafeConsole.Console.flush();
                }
                else
                {
                    byte[] buffer = state.rTerminal.processRawData(state.buffer);
                    if (buffer.All((x) => x == 0x00))
                    {
                        ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("No character data."));
                        ThreadSafeConsole.Console.flush();
                    }
                    else state.rTerminal.input(buffer);
                }
            }
            //Clear the buffer
            state.buffer = new byte[ServerUserObject.BufferSize];
            if (!socketHelper.isDisposed(state.workSocket)) state.workSocket.BeginReceive(state.buffer, 0, ServerUserObject.BufferSize, 0, new AsyncCallback(readCallback), state);
        }
    }

    public class ServerUserObject
    {
        public Socket workSocket = null;
        public const int BufferSize = 1024;
        public byte[] buffer = new byte[BufferSize];
        //Interactive stuff
        public serverPage page;
        public socketTerminal rTerminal;
    }

    //TODO: implement width and height
    public interface pageControl
    {
        string onInput(byte[] ui);
        string render();
        string value { get; set; }
        void clear();
        serverPage parent { get; set; }
        int topLeftX { get; set; }
        int topLeftY { get; set; }
        int width { get; set; }
        int height { get; set; }
        bool input { get; set; }
        pageControl clone();
    }

    //Technically a server page can be a page control
    //TODO: replace all terminal writes with string builder so ensures proper order
    //TODO: implement width and height
    public class serverPage
    {
        public List<pageControl> controls = new List<pageControl>();
        //public int selectedControl = 0;
        public socketTerminal terminal;
        private List<int> inputControls = null;
        private int inputControlsIndex = 0;
        public Action<serverPage> onResize;
        //private bool hasMoreTwoInputs = false;

        public serverPage(socketTerminal term)
        {
            terminal = term;

        }

        public void init()
        {
            for (int i = 0; i < controls.Count; i++)
            {
                controls[i].parent = this;
            }
            inputControls = controls.SelectIndex((x) => x.input).ToList();
            if (!(inputControls.Count > 0))
            {
                inputControlsIndex = 0;
                inputControls = null;
            }
        }

        public void render()
        {
            terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.clearScreenRestoreCursor + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
            for (int i = 0; i < controls.Count; i++)
            {
                render(controls[i]);
            }
        }

        public void render(pageControl control)
        {

            StringBuilder sb = new StringBuilder();
            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
            if (control.topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + control.topLeftX.ToString() + escapeCodeHelper.cursorRight);
            if (control.topLeftY > 0) sb.Append(escapeCodeHelper.escapeCode + control.topLeftY.ToString() + escapeCodeHelper.cursorDown);
            sb.Append(control.render());
            terminal.write(sb.ToString());
        }

        public void onInput(byte[] bytes)
        {
            if (inputControls == null) throw new Exception("Must be initialized");
            if (controls.Count <= 0) return;
            List<byte> curBytes = new List<byte>();
            int currentControlIndex = inputControls[inputControlsIndex];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x09 && controls.Count > 1)
                {
                    inputControlsIndex++;
                    if (inputControlsIndex > inputControls.Count - 1) inputControlsIndex = 0;
                    currentControlIndex = inputControls[inputControlsIndex];
                    //only update if there is data
                    if (curBytes.Any((x) => !((x == 0x09) || (x == 0x00))))
                    {
                        terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                        if (controls[currentControlIndex].topLeftX > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftX.ToString() + escapeCodeHelper.cursorRight);
                        if (controls[currentControlIndex].topLeftY > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftY.ToString() + escapeCodeHelper.cursorDown);
                        controls[inputControls[inputControlsIndex]].clear();
                        terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                        if (controls[currentControlIndex].topLeftX > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftX.ToString() + escapeCodeHelper.cursorRight);
                        if (controls[currentControlIndex].topLeftY > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftY.ToString() + escapeCodeHelper.cursorDown);
                        string ret = controls[currentControlIndex].onInput(curBytes.ToArray());
                        terminal.write(ret);
                        curBytes.Clear();
                    }
                }
                else
                {
                    curBytes.Add(bytes[i]);
                }
            }

            //only update if there is data
            if (curBytes.Any((x) => !((x == 0x09) || (x == 0x00)))) 
            {
                //Optimze to account current cursor position
                currentControlIndex = inputControls[inputControlsIndex];
                terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                if (controls[currentControlIndex].topLeftX > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftX.ToString() + escapeCodeHelper.cursorRight);
                if (controls[currentControlIndex].topLeftY > 0) terminal.write(escapeCodeHelper.escapeCode + controls[currentControlIndex].topLeftY.ToString() + escapeCodeHelper.cursorDown);
                controls[currentControlIndex].clear();
                string r = controls[currentControlIndex].onInput(curBytes.ToArray());
                terminal.write(r);
            }
        }

        public int getNextX(int index)
        {
            if (index < 0) return 0;
            if (controls.Count > 0) 
            {
                return controls[index].width + 1;
            }
            else return 0;
        }

        public int getNextX()
        {
            return getNextX(controls.Count - 1);
        }

        public int getNextY(int index)
        {
            if (index < 0) return 0;
            if (controls.Count > 0)
            {
                if (index == 0)
                {
                    return controls[index].height + 1;
                }

                int ret = 0;
                for (int i = 0; i < index; i++)
                {
                    ret += controls[i].height;
                }
                return ret + 1;
            }
            else return 0;
        }

        public int getNextY()
        {
            return getNextY(controls.Count - 1);
        }

        public serverPage clone(socketTerminal term)
        {
            serverPage page = new serverPage(term);
            page.controls = controls.Select((x) => x.clone()).ToList();
            page.onResize = onResize;
            return page;
        }
    }

    public static class defaultControls
    {
        public class text : pageControl
        {
            public string value { get; set; }
            public serverPage parent { get; set; }
            public int topLeftX { get; set; }
            public int topLeftY { get; set; }
            public bool input { get; set; }

            private int _width;
            public int width
            {
                get { return _width; }
                set
                {
                    if (frame)
                    {
                        _width = value - 2;
                    }
                    else
                    {
                        _width = value;
                    }
                }
            }
            private int _height;
            public int height
            {
                get { return _height; }
                set
                {
                    if (frame)
                    {
                        _height = value - 2;
                    }
                    else
                    {
                        _height = value;
                    }
                }
            }

            public bool frame { get; set; }
            public string[] corners = { escapeCodeHelper.uniFormDoubleTopLeft, escapeCodeHelper.uniFormDoubleTopRight, escapeCodeHelper.uniFormDoubleBottomLeft, escapeCodeHelper.uniFormDoubleBottomRight};
            private List<string> lines = new List<string>();
            //Since im too lazy to do lock statements
            private readonly SemaphoreSlim lineLock = new SemaphoreSlim(1, 1);
            private readonly SemaphoreSlim cornerLock = new SemaphoreSlim(1, 1);

            public string onInput(byte[] input)
            {
                parent.terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                if (topLeftX > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                if (topLeftY > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftY.ToString() + escapeCodeHelper.cursorDown);
                return render();
            }

            public text(int x, int y, int w, int h, bool frm)
            {
                topLeftX = x;
                topLeftY = y;
                value = "";
                input = false;
                width = w;
                height = h;
                if (frm)
                {
                    width -= 2;
                    height -= 2;
                }
                //So it doesn't trigger the property stuff
                frame = frm;
            }

            //If you want to change the xy value then change it yourself
            public pageControl clone()
            {
                if (frame) return new text(topLeftX, topLeftY, width + 2, height + 2, frame).set((t) => { t.value = value; return t; });
                else return new text(topLeftX, topLeftY, width, height, frame).set((t) => { t.value = value; return t; });
            }

            public string render()
            {
                lineLock.Wait();
                cornerLock.Wait();
                if (!frame)
                {
                    //List<string> lines = new List<string>();
                    bool reset = false;
                    if(lines.Count == 0)
                    {
                        reset = true;
                        string line = escapeCodeHelper.uniFormDoubleVertical;
                        for (int i = 0; i < value.Length; i++)
                        {
                            line += value[i];
                            if (i % (width) == 0 && i != 0)
                            {
                                lines.Add(line);
                                line = escapeCodeHelper.uniFormDoubleVertical;
                            }
                        }
                    }

                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        sb.Append(lines[i]);
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        if (i >= height) 
                        {
                            if (reset) lines = new List<string>();
                            lineLock.Release();
                            cornerLock.Release();
                            return sb.ToString();
                        }
                        if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                        if (i != lines.Count - 1) sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    }

                    if (reset) lines = new List<string>();
                    lineLock.Release();
                    cornerLock.Release();
                    return sb.ToString();
                }
                if (value.Length + 2 > width || lines.Count > 1) 
                {
                    //Extend frame bottom y by Math.Ceiling((value.Length + 2)/width)
                    //List<string> lines = new List<string>();
                    bool reset = false;
                    if (lines.Count == 0)
                    {
                        reset = true;
                        string line = escapeCodeHelper.uniFormDoubleVertical;
                        for (int i = 0; i < value.Length; i++)
                        {
                            line += value[i];
                            if (i % (width) == 0 && i != 0)
                            {
                                lines.Add(line);
                                line = escapeCodeHelper.uniFormDoubleVertical;
                            }
                        }

                        if(value.Length % width != 0) lines.Add(value.Substring((int)(Math.Floor((double)value.Length / (double)width) * width), value.Length - (int)(Math.Floor((double)value.Length / (double)width) * width)));
                    }

                    StringBuilder sb = new StringBuilder();

                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopLeft);
                    for (int i = 0; i < width; i++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                        sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                        sb.Append(lines[i]);
                        sb.Append(escapeCodeHelper.escapeCode + "999" + escapeCodeHelper.cursorRight);
                        if (width < parent.terminal.width && width + 2 != parent.terminal.width)
                        {
                            sb.Append(escapeCodeHelper.escapeCode + (parent.terminal.width - width - 2).ToString() + escapeCodeHelper.cursorLeft);
                        }
                        sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                        //sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                        if (i >= height + 1)
                        {
                            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                            if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                            sb.Append(escapeCodeHelper.uniFormDoubleBottomLeft);
                            for (int j = 0; j < width; j++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                            sb.Append(escapeCodeHelper.uniFormDoubleBottomRight);
                            if (reset) lines = new List<string>();
                            lineLock.Release();
                            cornerLock.Release();
                            return sb.ToString();
                        }
                        sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    }
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleBottomLeft);
                    for (int i = 0; i < width; i++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                    sb.Append(escapeCodeHelper.uniFormDoubleBottomRight);

                    if (reset) lines = new List<string>();
                    lineLock.Release();
                    cornerLock.Release();
                    return sb.ToString();
                }

                if (lines.Count == 1 && lines[0].Length + 2 > width)
                {
                    //Extend frame bottom y by Math.Ceiling((value.Length + 2)/width)
                    //List<string> lines = new List<string>();
                    StringBuilder sb = new StringBuilder();

                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopLeft);
                    for (int i = 0; i < width; i++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                        sb.Append(escapeCodeHelper.uniFormDoubleTopLeft);
                        sb.Append(lines[i]);
                        sb.Append(escapeCodeHelper.escapeCode + "999" + escapeCodeHelper.cursorRight);
                        sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                        sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                        if (i >= height)
                        {
                            sb.Append(escapeCodeHelper.uniFormDoubleBottomLeft);
                            for (int j = 0; j < width; j++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                            sb.Append(escapeCodeHelper.uniFormDoubleBottomRight);
                            lineLock.Release();
                            cornerLock.Release();
                            return sb.ToString();
                        }
                        if (i != lines.Count - 1) sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    }

                    lineLock.Release();
                    cornerLock.Release();
                    return sb.ToString();
                }

                if ((value.Length + 2 < width && lines.Count <= 1) || lines.Count == 1 )  {
                    if (lines.Count == 1) value = lines[0];
                    StringBuilder sb = new StringBuilder();
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopLeft);
                    for (int i = 0; i < width; i++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                    sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                    sb.Append(value);
                    //if (value.Length < width - 2) sb.Append(new String(' ', (width - 2) - value.Length));
                    //sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                    sb.Append(escapeCodeHelper.escapeCode + "999" + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleVertical);
                    //sb.Append(escapeCodeHelper.uniFormDoubleTopRight);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                    sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                    if (topLeftX > 0) sb.Append(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                    sb.Append(escapeCodeHelper.uniFormDoubleBottomLeft);
                    for (int i = 0; i < width; i++) sb.Append(escapeCodeHelper.uniFormDoubleHorizontal);
                    sb.Append(escapeCodeHelper.uniFormDoubleBottomRight);
                    lineLock.Release();
                    cornerLock.Release();
                    return sb.ToString();
                }

                lineLock.Release();
                cornerLock.Release();
                return escapeCodeHelper.escapeCode + escapeCodeHelper.colorRed + "Internal server error!" + escapeCodeHelper.escapeCode + escapeCodeHelper.reset;
            }

            public List<string> getLines()
            {
                lineLock.Wait();
                List<string> ret = new List<string>(lines);
                lineLock.Release();
                return ret;
            }

            public void setLines(List<string> nlines)
            {
                lineLock.Wait();
                if (nlines.Any((x) => x.Length + 2 > width)) 
                {
                    lineLock.Release();
                    throw new Exception("Line too long");
                }
                lines = nlines;
                lineLock.Release();
            }

            public void addLine(string str)
            {
                lineLock.Wait();
                if (str.Length + 2 > width) 
                {
                    if (lines.Count > 0)
                    {
                        string line = string.Empty;
                        for (int i = 0 + lines[lines.Count].Length; i < value.Length; i++)
                        {
                            line += str[i - lines[lines.Count].Length];
                            if (i % (width) == 0 && i != 0)
                            {
                                lines.Add(line);
                            }
                        }
                    }
                    if (lines.Count == 0)
                    {
                        string line = string.Empty;
                        for (int i = 0; i < value.Length; i++)
                        {
                            line += str[i];
                            if (i % (width) == 0 && i != 0)
                            {
                                lines.Add(line);
                            }
                        }
                    }
                }
                else
                {
                    lines.Add(str);
                }
                lineLock.Release();
            }

            public void setCorners(params string[] cs)
            {
                cornerLock.Wait();
                corners = cs.Take(corners.Length).ToArray();
                cornerLock.Release();
            }

            public void clear()
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.clearLine);

                if (!frame)
                {
                    if (lines.Count > 0)
                    {
                        for (int i = 1; i < lines.Count; i++)
                        {
                            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                            sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.clearLine);
                        }
                        parent.terminal.write(sb.ToString());
                        return;
                    }

                    if (value != "")
                    {
                        for (int i = 1; i < (int)Math.Ceiling((double)value.Length / width); i++)
                        {
                            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                            sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                            sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.clearLine);
                        }
                        parent.terminal.write(sb.ToString());
                        return;
                    }
                }

                if (lines.Count > 0)
                {
                    for (int i = 1; i < lines.Count; i++)
                    {
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.clearLine);
                    }
                }
                if (value != "")
                {
                    for (int i = 1; i < (int)Math.Ceiling((double)value.Length / (double)(width - 2)); i++)
                    {
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft);
                        sb.Append(escapeCodeHelper.escapeCode + "1" + escapeCodeHelper.cursorDown);
                        sb.Append(escapeCodeHelper.escapeCode + escapeCodeHelper.clearLine);
                    }
                }

                parent.terminal.write(sb.ToString());
                return;
            }
        }

        public class textInput : pageControl
        {
            private string _value;
            public string value
            {
                get { return _value; }
                set
                {
                    textControl.value = value;
                    _value = value;
                }
            }
            private serverPage _parent;
            public serverPage parent 
            {
                get { return _parent; }
                set
                {
                    textControl.parent = value;
                    _parent = value;
                }
            }
            private int _topLeftX;
            public int topLeftX
            {
                get { return _topLeftX; }
                set
                {
                    textControl.topLeftX = value;
                    _topLeftX = value;
                }
            }
            private int _topLeftY;
            public int topLeftY
            {
                get { return _topLeftY; }
                set
                {
                    textControl.topLeftY = value;
                    _topLeftY = value;
                }
            }
            public bool input { get; set; }
            public int width { get; set; }
            public int height { get; set; }

            public Action onClick;

            text textControl = new text(0, 0, 0, 0, false);

            public textInput(int x, int y, int w, int h)
            {
                textControl = new text(x, y, w, h, true);

                width = w;
                height = h;
                value = "";
                input = true;
            }

            public pageControl clone()
            {
                textInput cloned = new textInput(topLeftX, topLeftY, width, height);
                cloned.textControl = (text)textControl.clone();
                return cloned;
            }

            public string render()
            {
                textControl.value = escapeCodeHelper.escapeCode + escapeCodeHelper.reverseVideo + value + escapeCodeHelper.escapeCode + escapeCodeHelper.reset;
                return textControl.render();
            }

            public string onInput(byte[] _input)
            {
                List<byte> inp = _input.ToList();
                inp.RemoveAll((x) => x == 0x00);
                //TODO: Handle backspaces too
                //Assuming that backspace character is 0x7F
                int c = 0;
                for (int i = 0; i < inp.Count; i++)
                {
                    if (inp[i] == 0x7F)
                    {
                        if (value.Length - 1 < 0) continue;
                        value = value.Substring(0, value.Length - 1);
                        c++;
                    }
                    else if (inp[i] == 0x0D)
                    {
                        onClick?.Invoke();
                    }
                    else
                    {
                        value += Convert.ToChar(inp[i]);
                    }
                }
                textControl.value = escapeCodeHelper.escapeCode + escapeCodeHelper.reverseVideo + value + escapeCodeHelper.escapeCode + escapeCodeHelper.reset;
                for (int i = 0; i < c; i++)
                {
                    textControl.value += " ";
                }
                //textControl.clear();
                return textControl.render();
            }

            public void clear()
            {
                textControl.clear();
                parent.terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                if (topLeftX > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                if (topLeftY > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftY.ToString() + escapeCodeHelper.cursorDown);
            }
        }

        public class button : pageControl
        {
            private string _value;
            public string value
            {
                get { return _value; }
                set
                {
                    textControl.value = value;
                    _value = value;
                }
            }

            private serverPage _parent;
            public serverPage parent
            {
                get { return _parent; }
                set
                {
                    textControl.parent = value;
                    _parent = value;
                }
            }
            public int topLeftX { get; set; }
            public int topLeftY { get; set; }
            public bool input { get; set; }
            public int width { get; set; }
            public int height { get; set; }

            text textControl = new text(0, 0, 0, 0, false);//Prevents errors

            public Action onClick;

            public button(int x, int y, int w, int h)
            {
                textControl = new text(x, y, w, h, true);

                topLeftX = x;
                topLeftY = y;
                width = w;
                height = h;
                value = "";
                input = true;
            }

            public button(int x, int y, string val)
            {
                width = val.Length+3;
                height = 4;
                topLeftX = x;
                topLeftY = y;
                textControl = new text(x, y, width, height, true);

                value = val;
                input = true;
            }

            public pageControl clone()
            {
                button cloned = new button(topLeftX, topLeftY, width, height);
                cloned.value = value;
                cloned.textControl = (text)textControl.clone();
                cloned.onClick = onClick;
                return cloned;
            }

            public string onInput(byte[] bytes)
            {
                if (bytes.Any((x) => x == 0x0D)) onClick?.Invoke();
                return render();
            }


            public string render()
            {
                return textControl.render();
            }

            public void clear()
            {
                textControl.clear();
                parent.terminal.write(escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeLeft + escapeCodeHelper.escapeCode + escapeCodeHelper.cursorHomeUp);
                if (topLeftX > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftX.ToString() + escapeCodeHelper.cursorRight);
                if (topLeftY > 0) parent.terminal.write(escapeCodeHelper.escapeCode + topLeftY.ToString() + escapeCodeHelper.cursorDown);
            }
        }
    }

    //Get unicode dump
    //printf ╔| hexdump
    //print
    //printf '\xE2\x95\x94'
    static class escapeCodeHelper
    {
        //Normal Commands
        //EscapeCode + Command
        public static string escapeCode               = fromBytes(0x1B, 0x5B);
        public static string reset                    = fromBytes(0x30, 0x6D);
        public static string reverseVideo             = fromBytes(0x37, 0x6D);
        public static string clearScreenRestoreCursor = fromBytes(0x32, 0x4A);
        public static string clearLine                = fromBytes(0x32, 0x4B);
        //Cursor commands
        //EscapeCode + number + direction
        public static string cursorUp        = fromBytes(0x41);
        public static string cursorDown      = fromBytes(0x42);
        public static string cursorLeft      = fromBytes(0x44);
        public static string cursorRight     = fromBytes(0x43);
        public static string cursorHomeLeft  = fromBytes(0x39, 0x39, 0x39, 0x44); //No terminal should be 999 characters wide
        public static string cursorHomeUp    = fromBytes(0x39, 0x39, 0x39, 0x41); //No terminal should be 999 characters tall
        //Cursor position set
        //EscapeCode + Row + ; + Column + cursorPosition
        public static string cursorPosition = fromBytes(0x66);
        //Colors
        public static string colorBlack     = fromBytes(0x30, 0x3B, 0x33, 0x30, 0x6D);
        public static string colorRed       = fromBytes(0x30, 0x3B, 0x33, 0x31, 0x6D);
        public static string colorGreen     = fromBytes(0x30, 0x3B, 0x33, 0x32, 0x6D);
        public static string colorYellow    = fromBytes(0x30, 0x3B, 0x33, 0x33, 0x6D);
        public static string colorBlue      = fromBytes(0x30, 0x3B, 0x33, 0x34, 0x6D);
        public static string colorPurple    = fromBytes(0x30, 0x3B, 0x33, 0x35, 0x6D);
        public static string colorCyan      = fromBytes(0x30, 0x3B, 0x33, 0x36, 0x6D);
        public static string colorLightGray = fromBytes(0x30, 0x3B, 0x33, 0x37, 0x6D);

        //Unicode
        //No escape code (just send it)
        public const string uniUpArrow                = "⇧";
        public const string uniDownArrow              = "⇩";
        //Menus
        //Double
        public const string uniFormDoubleHorizontal   = "═";
        public const string uniFormDoubleVertical     = "║";
        public const string uniFormDoubleTopRight     = "╗";
        public const string uniFormDoubleBottomRight  = "╝";
        public const string uniFormDoubleTopLeft      = "╔";
        public const string uniFormDoubleBottomLeft   = "╚";
        public const string uniFormDoubleLeftBranch   = "╠";
        public const string uniFormDoubleRightBranch  = "╣";
        public const string uniFormDoubleTopBranch    = "╦";
        public const string uniFormDoubleBottomBranch = "╩";
        public const string uniFormDoubleCenterBranch = "╬";
        //Single
        public const string uniFormSingleHorizontal   = "─";
        public const string uniFormSingleVertical     = "│";
        public const string uniFormSingleTopRight     = "┐";
        public const string uniFormSingleBottomRight  = "┘";
        public const string uniFormSingleTopLeft      = "┌";
        public const string uniFormSingleBottomLeft   = "└";
        public const string uniFormSingleLeftBranch   = "├";
        public const string uniFormSingleRightBranch  = "┤";
        public const string uniFormSingleTopBranch    = "┬";
        public const string uniFormSingleBottomBranch = "┴";
        public const string uniFormSingleCenterBranch = "┼";

        public static string fromBytes(params int[] p)
        {
            string ret = string.Empty;
            for (int i = 0; i < p.Length; i++)
            {
                ret += Char.ToString(Convert.ToChar(p[i]));
            }
            return ret;
        }
    }

    public class socketTerminal
    {
        Socket socket;
        public Action<byte[]> onInput = null;
        public Action onResize = null;
        public int width = 0;
        public int height = 0;

        //Threading stuff
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim inputLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim sendReturnLock = new SemaphoreSlim(1, 1);

        public socketTerminal(Socket _socket)
        {
            socket = _socket;
            //write("Type in a terminal width and height in the format '<WIDTH>x<HEIGHT>'");
            //Assume its a 80x24
            width = 80;
            height = 24;
        }

        public void write(string str)
        {
            try
            {
                sendLock.Wait();
                byte[] bytes = Encoding.Default.GetBytes(str);
                socket.BeginSend(bytes, 0, bytes.Length, 0, new AsyncCallback(sendFinish), socket);
                //if (!async) sendReturnLock.Wait();
            }
            catch (Exception)
            {
                ThreadSafeConsole.Console.schedule(
                    () => (!socketHelper.isDisposed(socket)).Do(
                        () => { ThreadSafeConsole.Console.writeLine("Error sending to " + socket.RemoteEndPoint + ", shutting down socket..."); }));
                ThreadSafeConsole.Console.flush();
                //socketHelper.tryShutdown(socket);
            }
        }

        public void write(string str, bool async)
        {
            try
            {
                sendLock.Wait();
                byte[] bytes = Encoding.Default.GetBytes(str);
                socket.BeginSend(bytes, 0, bytes.Length, 0, new AsyncCallback(sendFinish), socket);
                if (!async) sendReturnLock.Wait();
            }
            catch (Exception)
            {
                ThreadSafeConsole.Console.schedule(
                    () => (!socketHelper.isDisposed(socket)).Do(
                        () => { ThreadSafeConsole.Console.writeLine("Error sending to " + socket.RemoteEndPoint + ", shutting down socket..."); }));
                ThreadSafeConsole.Console.flush();
                //socketHelper.tryShutdown(socket);
            }
        }

        public void writeBytes(params byte[] bytes)
        {
            try
            {
                sendLock.Wait();
                socket.BeginSend(bytes, 0, bytes.Length, 0, new AsyncCallback(sendFinish), socket);
            }
            catch (Exception)
            {
                ThreadSafeConsole.Console.schedule(
                    () => (!socketHelper.isDisposed(socket)).Do(
                     () => { ThreadSafeConsole.Console.writeLine("Error sending to " + socket.RemoteEndPoint + ", shutting down socket..."); }));
                ThreadSafeConsole.Console.flush();
                //socketHelper.tryShutdown(socket);
            }
        }

        private void sendFinish(IAsyncResult result)
        {
            try
            {
                int byteSent = socket.EndSend(result);
                ThreadSafeConsole.Console.schedule(
                    () => (!socketHelper.isDisposed(socket)).Do(
                        () => { ThreadSafeConsole.Console.writeLine("Sent " + byteSent + " bytes to " + socket.RemoteEndPoint); }));
                ThreadSafeConsole.Console.flush();
                sendLock.Release();
                sendReturnLock.Release();
            }
            catch (Exception)
            {
                //ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine("Error finishing send"));
                //ThreadSafeConsole.Console.flush();
                //socketHelper.tryShutdown(socket);
            }
        }

        public void input(byte[] inp)
        {
            inputLock.Wait();
            if (!socketHelper.isDisposed(socket)) ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine(String.Format("{0} -> CHARACTER {1}", socket.RemoteEndPoint, "\"" + ASCIIEncoding.ASCII.GetString(inp) + "\"")));
            ThreadSafeConsole.Console.flush();
            onInput?.Invoke(inp);
            inputLock.Release();
        }

        //IAC  SB   NAWS <Shifted Width>  <Width>  <Shifted Height>  <Height>  IAC  SE
        //255  250  31   <Shifted Width>  <Width>  <Shifted Height>  <Height>  255  240
        //Width = <Shifted Width> << 8 + <Width>
        public static socketHelper.scanSimple.term[] naws = { new socketHelper.scanSimple.term(0xFF, false), new socketHelper.scanSimple.term(0xFA, false), new socketHelper.scanSimple.term(0x1F, false), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(null, true), new socketHelper.scanSimple.term(0xFF, false), new socketHelper.scanSimple.term(0xF0, false) };
        private List<byte> IACMessageBuffer = new List<byte>();

        public byte[] processRawData(byte[] buffer)
        {
            byte[] newBuffer = new byte[buffer.Length];
            Array.Copy(buffer, newBuffer, buffer.Length);
            int start = socketHelper.scanSimple.getStart(naws, buffer);
            if (start != -1)
            {
                if (start + naws.Length > buffer.Length)
                {
                    //The message is chopped!
                    //Store in IAC message buffer
                    IACMessageBuffer.AddRange(buffer.Skip(start).Take(buffer.Length - start));
                    //Remove from the buffer
                    Array.Copy(buffer, 0, newBuffer, 0, start);
                }
                if (start + naws.Length <= buffer.Length)
                {
                    //The message is fully in the current buffer
                    socketHelper.scanSimple.term[] terms = socketHelper.scanSimple.scan(naws, buffer);
                    //This should not happen
                    if (terms.Length == 0) throw new Exception("Internal error");
                    //get all the parameters
                    byte[] values = terms.Where((x) => x.isQuery).Select((x) => Convert.ToByte(x.value)).ToArray();
                    //calculate width and height
                    int clientWidth = (values[0] << 8) + values[1];
                    int clientHeight = (values[2] << 8) + values[3];
                    ThreadSafeConsole.Console.schedule(
                        () => { (!socketHelper.isDisposed(socket)).Do(
                            () => { ThreadSafeConsole.Console.writeLine(String.Format("{0} -> NAWS {1}x{2}", socket.RemoteEndPoint, clientWidth, clientHeight)); }); });
                    ThreadSafeConsole.Console.flush();
                    width = clientWidth;
                    height = clientHeight;
                    newBuffer = new byte[start];
                    //Since its in the buffer copy everything but it
                    Array.Copy(buffer, 1024 - start + naws.Length, newBuffer, 0, start - naws.Length);
                    //Not needed
                    IACMessageBuffer.Clear();
                    //Tell the attached page that a resize happened
                    onResize?.Invoke();
                    return newBuffer;
                }
            }
            //TODO: FIX CODE, COMPLETELY BROKEN
            else if (IACMessageBuffer.Count > 0)
            {
                IACMessageBuffer.AddRange(buffer);
                if (start + naws.Length > buffer.Length)
                {
                    //Full message isnt in the buffer
                    Array.Copy(buffer, 0, newBuffer, 0, start);
                    IACMessageBuffer.AddRange(buffer.Skip(start).Take(buffer.Length - start));
                    return newBuffer;
                }
                //The message is fully in the current buffer
                socketHelper.scanSimple.term[] terms = socketHelper.scanSimple.scan(naws, IACMessageBuffer.ToArray());
                //This should not happen
                if (terms.Length == 0) throw new Exception("Internal error");
                //get all the parameters
                byte[] values = terms.Where((x) => x.isQuery).Select((x) => Convert.ToByte(x.value)).ToArray();
                //calculate width and height
                int clientWidth = (values[0] << 8) + values[1];
                int clientHeight = (values[2] << 8) + values[3];
                //TODO remove this
                ThreadSafeConsole.Console.schedule(
                    () => { (!socketHelper.isDisposed(socket)).Do(
                        () => { ThreadSafeConsole.Console.writeLine(String.Format("{0} -> NAWS {1}x{2}", socket.RemoteEndPoint, clientWidth, clientHeight)); }); });
                ThreadSafeConsole.Console.flush();
                width = clientWidth;
                height = clientHeight;
                newBuffer = new byte[start];
                Array.Copy(buffer, 0, newBuffer, 0, start);
                //Clear the buffer
                IACMessageBuffer.Clear();
                //Tell the attached page that a resize happened
                onResize?.Invoke();
            }

            //System.Console.Clear();
            //System.Console.WriteLine(socketHelper.hexDump(buffer, 8));
            //string hexDump = socketHelper.hexDump(buffer, 8);
            //ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.writeLine(hexDump));
            //ThreadSafeConsole.Console.schedule(() => ThreadSafeConsole.Console.shiftdown(hexDump.Split('\n').Length));
            //ThreadSafeConsole.Console.flush();
            return newBuffer;
        }
    }

    public static class socketHelper
    {
        public static void tryShutdown(Socket socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
            }
            catch (Exception)
            {
                return;
            }
        }

        public static bool isDisposed(Socket socket)
        {
            try
            {
                string s = socket.RemoteEndPoint.ToString();
                return s.Length < 0;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        public static class scanSimple
        {
            public static term[] scan(term[] pattern, byte[] buffer)
            {
                if (buffer.Length < pattern.Length) return new term[0];
                term[] ret = new term[pattern.Length];
                int currentPattern = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (!pattern[currentPattern].equals(buffer[i]))
                    {
                        //Count escaped patterns like:
                        //IAC IAC DATA
                        //AS: IAC DATA
                        //if (i > pattern.Length - 1 && currentPattern > 0) i -= (pattern.Length - 2);
                        currentPattern = 0;
                    }
                    if (pattern[currentPattern].equals(buffer[i]))
                    {
                        ret[currentPattern] = new term(buffer[i], pattern[currentPattern].isQuery);
                        currentPattern++;
                    }
                    if (currentPattern == pattern.Length) return ret;
                }
                if (currentPattern != pattern.Length) return new term[0];
                return ret;
            }

            public static int getStart(term[] pattern, byte[] buffer)
            {
                int currentPattern = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (!pattern[currentPattern].equals(buffer[i]))
                    {
                        //Count escaped patterns like:
                        //IAC IAC DATA DATA
                        //AS: IAC DATA
                        //if (i > pattern.Length - 1 && currentPattern > 0) i -= (pattern.Length - 2);
                        currentPattern = 0;
                    }
                    if (pattern[currentPattern].equals(buffer[i]))
                    {
                        currentPattern++;
                    }
                    if (currentPattern == pattern.Length) return buffer.Length - currentPattern;
                }
                if (currentPattern == 0) return -1;
                return buffer.Length - currentPattern;
            }

            public class term
            {
                public object value;
                public bool isQuery;

                public term(object v, bool q)
                {
                    value = v;
                    isQuery = q;
                }

                public bool equals(object s)
                {
                    return isQuery || ((byte)s).Equals(Convert.ToByte(value));
                }
            }
        }
    }

    public static class extensions
    {
        public static T Do<T>(this bool b, Func<T> action)
        {
            if (!b) return default(T);
            return action();
        }

        public static void Do(this bool b, Action action)
        {
            if (!b) return;
            action();
        }

        public static int indexFirst<T>(this IEnumerable<T> enumerable, Func<T, bool> predicate)
        {
            int c = 0;
            foreach (T item in enumerable)
            {
                if (predicate(item)) return c;
                else c++;
            }
            return -1;
        }

        public static IEnumerable<int> SelectIndex<T>(this IEnumerable<T> enumerable, Func<T, bool> predicate)
        {
            int c = 0;
            foreach (T item in enumerable)
            {
                if (predicate(item))
                {
                    yield return c;
                    c++;
                }
                else c++;
            }
        }

        public static bool CountAtLeast<T>(this IEnumerable<T> enumerable, int minCount)
        {
            if (enumerable == null) throw new Exception("source");
            ICollection<T> collectionoft = enumerable as ICollection<T>;
            if (collectionoft != null) return collectionoft.Count >= minCount;
            int count = 0;
            using (IEnumerator<T> e = enumerable.GetEnumerator())
            {
                checked
                {
                    while (e.MoveNext()) 
                    {
                        count++;
                        if (count >= minCount) return true;
                    }
                }
            }
            return false;
        }

        public static T set<T>(this T obj, Func<T,T> setfunc)
        {
            return setfunc(obj);
        }
    }
}
