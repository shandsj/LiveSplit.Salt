using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LiveSplit.Salt
{
    public class ProgramPointer
    {
        // Only scan one process memory map at a time. Each scan reads large
        // executable regions, so parallel scans create unnecessary memory and CPU pressure.
        private static readonly SemaphoreSlim ScanSemaphore = new SemaphoreSlim(1, 1);
        private readonly string _signature;
        private readonly int _offset;
        private readonly int _derefCount;

        private IntPtr _ptr;
        private int _id = -1;

        private DateTime _nextTry = DateTime.MinValue;
        private Task<IntPtr> _pointerScan;
        private int _pointerScanProcessId = -1;

        public ProgramPointer(string signature, int offset, int derefCount)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                throw new ArgumentNullException(nameof(signature));
            }

            _signature = signature;
            _offset = offset;
            _derefCount = derefCount;
        }

        public bool IsReady(Process program)
        {
            GetPointer(program);
            return _ptr != IntPtr.Zero;
        }

        public T Read<T>(Process program, params int[] offsets) where T : struct
        {
            GetPointer(program);
            if (_ptr == IntPtr.Zero || program == null)
            {
                return default;
            }

            try
            {
                return program.Read<T>(_ptr, offsets);
            }
            catch (InvalidOperationException)
            {
                // The game process may exit or its Process handle may be disposed
                // between pointer resolution and the memory read.
                Reset();
                return default;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Reset();
                return default;
            }
        }

        private void GetPointer(Process program)
        {
            if (program == null)
            {
                Reset();
                return;
            }

            int processId;
            try
            {
                processId = program.Id;
            }
            catch (InvalidOperationException)
            {
                Reset();
                return;
            }

            // A new game process needs a fresh pointer scan. Any scan already in
            // progress owns a separate Process handle and can safely finish.
            if (processId != _id)
            {
                _id = processId;
                _ptr = IntPtr.Zero;
                _pointerScan = null;
                _pointerScanProcessId = -1;
                _nextTry = DateTime.MinValue;
            }

            if (_ptr != IntPtr.Zero)
            {
                return;
            }

            if (_pointerScan != null)
            {
                if (_pointerScanProcessId != processId)
                {
                    _pointerScan = null;
                    _pointerScanProcessId = -1;
                }
                else if (!_pointerScan.IsCompleted)
                {
                    return;
                }
                else
                {
                    Task<IntPtr> completedScan = _pointerScan;
                    _pointerScan = null;
                    _pointerScanProcessId = -1;

                    if (completedScan.Status == TaskStatus.RanToCompletion)
                    {
                        _ptr = completedScan.Result;
                    }
                    else
                    {
                        // Observe failures (for example, if the game exits while
                        // scanning) and retry after the normal scan cooldown.
                        _ = completedScan.Exception;
                        _nextTry = DateTime.Now.AddSeconds(1);
                        return;
                    }

                    if (_ptr == IntPtr.Zero)
                    {
                        return;
                    }

                    // Resolve the signature's pointer chain after the scan has
                    // completed. These are only a handful of memory reads.
                    try
                    {
                        _ptr += _offset;
                        for (int i = 0; i < _derefCount; i++)
                        {
                            _ptr = (IntPtr)program.Read<uint>(_ptr);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Reset();
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        Reset();
                    }

                    return;
                }
            }

            if (DateTime.Now < _nextTry)
            {
                return;
            }

            _nextTry = DateTime.Now.AddSeconds(1);
            _pointerScanProcessId = processId;

            // Signature scans can read large portions of the game's memory.
            // Never perform that work from LiveSplit's UI/update thread.
            _pointerScan = Task.Run(() =>
            {
                ScanSemaphore.Wait();
                try
                {
                    using (Process scanProcess = Process.GetProcessById(processId))
                    {
                        MemorySearcher searcher = new MemorySearcher
                        {
                            MemoryFilter = info =>
                                (info.State & 0x1000) != 0 &&
                                (info.Protect & 0x40) != 0 &&
                                (info.Protect & 0x100) == 0
                        };

                        return searcher.FindSignature(scanProcess, _signature);
                    }
                }
                finally
                {
                    ScanSemaphore.Release();
                }
            });
            _pointerScan.ContinueWith(
                failedScan => { _ = failedScan.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void Reset()
        {
            _id = -1;
            _ptr = IntPtr.Zero;
            _pointerScan = null;
            _pointerScanProcessId = -1;
            _nextTry = DateTime.MinValue;
        }
    }
}
