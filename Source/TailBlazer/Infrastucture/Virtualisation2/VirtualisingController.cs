using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using TailBlazer.Domain.FileHandling;
using TailBlazer.Domain.Infrastructure;
using TailBlazer.Views.Tail;

namespace TailBlazer.Infrastucture.Virtualisation2
{
    public class VirtualisingController: IVirtualController<LineProxy>, IDisposable
    {
        private readonly ILineProxyFactory _proxyFactory;
        private readonly ISourceCache<LineProxy, int> _source = new SourceCache<LineProxy, int>(lp => lp.Index);
        private readonly IDisposable _cleanUp;
        private readonly int _pageSize = 200;
        private readonly object _locker = new object();
        private ILineProvider _lineProvider;
        private int _count =-1;

        public IObservable<int> CountChanged { get; }
        public IObservable<ItemWithIndex<LineProxy>[]> ItemsAdded { get; }

        public VirtualisingController(ILineProxyFactory proxyFactory, IObservable<ILineProvider> source, ISchedulerProvider schedulerProvider)
        {
            _proxyFactory = proxyFactory;       
            
            //1. when LP has changed, need to cache and update observable collection [tailing]
            //2. Scroll is governed by GetIndex();
            var initialTail = source.Synchronize(_locker).Publish();


            var loader = initialTail.Subscribe(lp =>
            {
                _lineProvider = lp;

                var previousCount = _count;
                _count = lp.Count;

                //for initial, load page and update collection
                var scrollRequest = previousCount == -1
                    ? new ScrollRequest(_pageSize)
                    : new ScrollRequest(_count - previousCount + 1);

                var lines = LoadPage(scrollRequest);
                _source.AddOrUpdate(lines);
            });
            
            ItemsAdded = _source.Connect()
                .Select(changes =>
                {
                    return changes
                        .Where(c => c.Reason == ChangeReason.Add)
                        .Select(lp => new ItemWithIndex<LineProxy>(lp.Current, lp.Current.Index))
                        .ToArray();
                })
                .ObserveOn(schedulerProvider.MainThread);

            CountChanged = initialTail.Select(lp => lp.Count).ObserveOn(schedulerProvider.MainThread); ;

            _cleanUp = new CompositeDisposable(_source, loader, initialTail.Connect());
        }



        private IEnumerable<LineProxy> LoadPage(ScrollRequest scrollRequest)
        {
            return _lineProvider
                .ReadLines(scrollRequest)
                .Select(_proxyFactory.Create);
        }

        public LineProxy Get(int index)
        {
            var item = _source.Lookup(index);
            if (item.HasValue)
                return item.Value;

            //try loading value
            var firstValue = Math.Max(0, index - _pageSize);

            var lines = LoadPage(new ScrollRequest(ScrollReason.User,_pageSize, firstValue) );
            _source.AddOrUpdate(lines);

            return _source.Lookup(index).ValueOr(() => null);
        }
        
        public int IndexOf(LineProxy item)
        {
            return _source.Lookup(item.Index)
                .ConvertOr(lp => lp.Index,() => -1);
        }

        public void Dispose()
        {
            _cleanUp.Dispose();
        }

        public int Count()
        {
            return _count;
        }
    }
}