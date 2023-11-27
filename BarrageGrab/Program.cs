using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BarrageGrab
{
    public class Program
    {
        // Import necessary functions from user32.dll
        //[DllImport("user32.dll")]
        //private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        //// Delegate type to be used as the handler routine for SetConsoleCtrlHandler
        //private delegate bool ConsoleCtrlDelegate(int ctrlType);

        static void Main(string[] args)
        {            
            if (CheckAlreadyRunning())
            {
                Console.WriteLine("已经有一个监听程序在运行，按任意键退出...");
                Console.ReadKey();
                return;
            }

            try
            {
                // Set the console control handler to handle Ctrl+C and close events
                SetConsoleCtrlHandler(ConsoleCtrlHandler, true);

                WinApi.DisableQuickEditMode();//禁用控制台快速编辑模式            
                Console.Title = "抖音弹幕监听推送";

                bool exited = false;
                AppRuntime.WssService.StartListen();
                AppRuntime.WssService.OnClose += (s, e) =>
                {
                    //退出程序
                    exited = true;
                };

                while (!exited)
                {
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine("服务器已关闭...");

            //退出程序,不显示 按任意键退出
            Environment.Exit(0);
        }

        // Handler function for Ctrl+C and close events
        private static bool ConsoleCtrlHandler(int ctrlType)
        {
            Console.WriteLine("Closing the application...");

            AppRuntime.WssService.Close();
            // Additional cleanup or finalization code can be added here

            //Thread.Sleep(1000); // Simulating some cleanup process

            Console.WriteLine("End of the application.");

            // Return false to continue normal termination process
            return false;
        }


        private static WinApi.ControlCtrlDelegate cancelHandler = new WinApi.ControlCtrlDelegate((CtrlType) =>
        {
            switch (CtrlType)
            {
                case 0:
                    //Console.WriteLine("0工具被强制关闭"); //Ctrl+C关闭  
                    //server.Close();
                    break;
                case 2:
                    Console.WriteLine("2工具被强制关闭");//按控制台关闭按钮关闭
                    AppRuntime.WssService.Close();
                    break;
            }
            return false;
        });

        //检测程序是否多开
        private static bool CheckAlreadyRunning()
        {
            const string mutexName = "DyBarrageGrab";           
            // Try to create a new named mutex.
            using (Mutex mutex = new Mutex(true, mutexName, out bool createdNew))
            {
                return !createdNew;
            }
        }

        // Import necessary functions from kernel32.dll
        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        // Delegate type to be used as the handler routine for SetConsoleCtrlHandler
        private delegate bool ConsoleCtrlDelegate(int ctrlType);
    }
}
