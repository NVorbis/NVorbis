/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;

namespace NVorbis
{
    static class ACache
    {
        [ThreadStatic]
        static bool InScope = false;

        static List<Action> ScopeCallbacks = new List<Action>();

        static class BufferStorage<T>
        {
            class Item
            {
                internal T[] Array;
                internal bool InUse;
            }

            static List<Item> _backingStore = new List<Item>();

            [ThreadStatic]
            static List<Item> _scopeItems = new List<Item>();

            static BufferStorage()
            {
                ACache.ScopeCallbacks.Add(CloseScope);
            }

            static List<Item> ScopeItems
            {
                get
                {
                    return _scopeItems ?? (_scopeItems = new List<Item>());
                }
            }

            internal static T[] Get(int elements, bool clearFirst)
            {
                Item item;

                for (int i = 0; i < _backingStore.Count; i++)
                {
                    if (!_backingStore[i].InUse && _backingStore[i].Array.Length == elements)
                    {
                        item = _backingStore[i];
                        item.InUse = true;
                        
                        if (clearFirst) Array.Clear(item.Array, 0, elements);

                        if (ACache.InScope) ScopeItems.Add(item);

                        return item.Array;
                    }
                }

                item = new Item { Array = new T[elements], InUse = true };
                _backingStore.Add(item);

                if (ACache.InScope) ScopeItems.Add(item);

                return item.Array;
            }

            internal static void Return(ref T[] buffer)
            {
                var array = buffer;

                for (int i = 0; i < _backingStore.Count; i++)
                {
                    if (_backingStore[i].Array == buffer)
                    {
                        _backingStore[i].InUse = false;

                        if (ACache.InScope) ScopeItems.Remove(_backingStore[i]);

                        break;
                    }
                }

                buffer = null;
            }

            internal static void CloseScope()
            {
                foreach (var item in ScopeItems)
                {
                    if (item != null) item.InUse = false;
                }
                ScopeItems.Clear();
            }
        }

        internal static T[] Get<T>(int elements)
        {
            return Get<T>(elements, true);
        }

        internal static T[] Get<T>(int elements, bool clearFirst)
        {
            return BufferStorage<T>.Get(elements, clearFirst);
        }

        internal static T[][] Get<T>(int firstRankSize, int secondRankSize)
        {
            var temp = BufferStorage<T[]>.Get(firstRankSize, true);
            for (int i = 0; i < firstRankSize; i++)
            {
                temp[i] = BufferStorage<T>.Get(secondRankSize, true);
            }
            return temp;
        }

        internal static T[][][] Get<T>(int firstRankSize, int secondRankSize, int thirdRankSize)
        {
            var temp = BufferStorage<T[][]>.Get(firstRankSize, true);
            for (int i = 0; i < firstRankSize; i++)
            {
                temp[i] = Get<T>(secondRankSize, thirdRankSize);
            }
            return temp;
        }

        internal static void Return<T>(ref T[] buffer)
        {
            BufferStorage<T>.Return(ref buffer);
        }

        internal static void Return<T>(ref T[][] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != null) Return<T>(ref buffer[i]);
            }
            BufferStorage<T[]>.Return(ref buffer);
        }

        internal static void Return<T>(ref T[][][] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != null) Return<T>(ref buffer[i]);
            }
            BufferStorage<T[][]>.Return(ref buffer);
        }

        internal static void BeginScope()
        {
            InScope = true;
        }

        internal static void EndScope()
        {
            InScope = false;

            foreach (var action in ScopeCallbacks)
            {
                action();
            }
        }
    }
}
