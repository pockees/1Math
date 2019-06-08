﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Excel = Microsoft.Office.Interop.Excel;
using System.Threading;
using System.Net.Http;
using System.Windows.Media;

namespace _1Math
{
    internal abstract class ExcelConcurrent : IHasReportor//定位：用于批量执行针对Excel.Range的任务
    {
        //source and results
        private Excel.Range _sourcesRange;
        private string[] _sources;
        private int _m;
        private int _n;
        private dynamic[] _results;

        //concurrent controller and progress reportor
        int _maxConcurrent = 2;
        private int MaxConcurrent
        {
            get => _maxConcurrent;
            set
            {
                if (value > 0)
                {
                    _maxConcurrent = Math.Min(value, _totalCount);
                    //这里的逻辑也要处理好，尽量不让开启的并发任务数大于总任务数，避免资源浪费
                    //当然，就算大于了，后面的GetNext方法也会让多余的Task安安静静地结束，不会有任何bug
                }
                else
                {
                    _maxConcurrent = Math.Min(System.Environment.ProcessorCount, _totalCount);
                    //输入零就自动确定线程数
                }
            }
        }
        public Reportor Reportor { get; private set; }
        private volatile int _totalCount;
        private volatile int _completedCount = 0;
        private void CompleteOneTask()
        {
            _completedCount++;
            Reportor.Report(_completedCount / (double)_totalCount);
            Reportor.Report($"已处理：{_completedCount}/{_totalCount}");
        }
        public ExcelConcurrent(Excel.Range range = null, int maxConcurrent = 0)
        {
            if (range == null)//子类省略了range参数，则默认为当前selection
            {
                range = Globals.ThisAddIn.Application.Selection as Excel.Range;
                if (range == null)//如果使用selection，则需要检测一下它是否为range，结合上面的as判断
                {
                    throw new Exception("InputIs'ntExcelRange");
                }
            }
            if (range.Areas.Count > 1)
            {
                throw new Exception("DisontinuousExcelRange");//不连续区域……竖的还好，横的就搞笑了，直接不允许用户这么干才是最好的
            }
            _sourcesRange = range;
            _m = _sourcesRange.Rows.Count;
            _n = _sourcesRange.Columns.Count;
            _totalCount = _m * _n;
            MaxConcurrent = maxConcurrent;
            Reportor = new Reportor(this);
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            Reportor.Report("初始化任务资源...");
            _sources = new string[_totalCount];
            int t = 0;//
            try
            {
                for (int i = 0; i < _m; i++)
                {
                    for (int j = 0; j < _n; j++)
                    {
                        _sources[t] = (string)_sourcesRange[i + 1, j + 1].Value;
                        t++;
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
#if DEBUG
            string t1 = stopwatch.Elapsed.TotalSeconds.ToString();
#endif
            //Build tasks
            _results = new dynamic[_totalCount];
            
            Task[] tasks = new Task[_maxConcurrent];
            for (int i = 0; i < _maxConcurrent; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                tasks[i] = WorkContinuouslyAsync(cancellationToken);
            }
#if DEBUG
            string t2 = stopwatch.Elapsed.TotalSeconds.ToString();
#endif
            //Wait for workers to finish
            await Task.WhenAll(tasks);
            string t3 = stopwatch.Elapsed.TotalSeconds.ToString();
            //Get results
            dynamic[,] results = new dynamic[_m, _n];
            t = 0;
            for (int i = 0; i < _m; i++)
            {
                for (int j = 0; j < _n; j++)
                {
                    results[i, j] = _results[t];
                    t++;
                }
            }
#if DEBUG
            string t4 = stopwatch.Elapsed.TotalSeconds.ToString();
#endif
            if (!cancellationToken.IsCancellationRequested)
            {
                _sourcesRange.Offset[0, _n].Value = results;
            }
            stopwatch.Stop();
            Reportor.Report($"耗时{stopwatch.Elapsed.TotalSeconds}秒，{_completedCount}/{_totalCount}");
#if DEBUG
            System.Windows.Forms.MessageBox.Show($"从Excel读数据{t1} 构建任务{t2} 执行任务{t3} 回写Excel{t4}");
#endif
        }

        //任务分配
        private const int NoNext = -1;
        private volatile int _builtCount = 0;
        private int GetNext()
        {
            int next;
            if (_builtCount < _totalCount)
            {
                next = _builtCount;
                _builtCount++;
            }
            else
            {
                next = NoNext;
            }
            return next;
        }

        //持续工作的线程
        private async Task WorkContinuouslyAsync(CancellationToken cancellationToken)
        {
            int next = GetNext();
            while (next != NoNext)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                string source = _sources[next];
                _results[next] = await WorkAsync(source, cancellationToken);
                CompleteOneTask();
                next = GetNext();//2019年6月7日脑残了，忘了加这句……
            }
        }
        protected abstract Task<dynamic> WorkAsync(string source, CancellationToken cancellationToken);
    }
    internal sealed class AccessibilityChecker:ExcelConcurrent
    {
        protected override async Task<dynamic> WorkAsync(string source, CancellationToken cancellationToken)
        {
            string url =(string)source;
            string accessibility;
            using (HttpClient checkClient = new HttpClient())
            {
                checkClient.Timeout = new TimeSpan(0, 0, 10);
                try
                {
                    var response = await checkClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    accessibility = response.IsSuccessStatusCode.ToString();
                }
                catch (Exception Ex)
                {
                    accessibility = Ex.Message;
                }
            }
            return accessibility;
        }
    }
    internal sealed class MediaDurationChecker : ExcelConcurrent
    {
        protected override async Task<dynamic> WorkAsync(string source, CancellationToken cancellationToken)
        {
            double duration;
            duration = await Task.Run(()=>
            {
                MediaPlayer mediaPlayer = new MediaPlayer();
                mediaPlayer.Open(new Uri(source));
                double result=0;
                DateTime start = DateTime.Now;
                TimeSpan timeSpan;
                do
                {
                    Thread.Sleep(50);
                    if (mediaPlayer.NaturalDuration.HasTimeSpan)
                    {
                        result = mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                        mediaPlayer.Stop();
                        mediaPlayer.Close();
                    }
                    timeSpan = DateTime.Now - start;
                } while (result == 0 && timeSpan.TotalSeconds < 10);
                return (result);
            });
            return duration;
        }
    }
}