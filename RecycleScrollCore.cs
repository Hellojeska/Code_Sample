using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 가변형 멀티 프리팹 재사용 스크롤 뷰의 핵심 최적화 로직 모음
/// (Zero-GC 할당 및 동적 사이즈 대응)
/// </summary>
public class UIRecycleScrollContent : UIBehaviour
{
    /// <summary>
    /// [핵심 로직 1. Zero-GC 멀티 프리팹 풀링 매핑]
    /// 스크롤 리스트를 초기화하거나 갱신할 때 발생하는 힙(Heap) 메모리 할당을 방지하기 위한 함수입니다.
    /// 배열이나 List 대신 stackalloc을 사용해 스택 메모리에 큐(Queue) 인덱스를 할당함으로써,
    /// 잦은 UI 갱신에도 가비지 컬렉터(GC) 스파이크가 발생하지 않도록 최적화했습니다.
    /// </summary>
    /// <param name="itemCount">전체 데이터 개수</param>
    /// <param name="func">인덱스에 해당하는 프리팹 종류(BaseItemIndex)를 반환하는 델리게이트</param>
    public virtual void BuildScrollItems(int itemCount, System.Func<int, int> func)
    {
        // 최적화 포인트: 멀티 프리팹 관리를 위한 인덱스 큐를 Stack 영역에 할당하여 GC 발생 원천 차단
        Span<int> instItemQueue = stackalloc int[m_instantiateItems.Length];

        for (int i = 0; i < itemCount; ++i)
        {
            // 현재 데이터가 어떤 종류의 프리팹을 사용해야 하는지 확인
            int baseItemIndex = func.Invoke(i);

            // 해당 프리팹의 오브젝트 풀(Pool) 가져오기
            List<InstantiateItem> instItems = m_instantiateItems[baseItemIndex];

            // 순환 큐 방식으로 사용할 풀링 객체의 인덱스 계산
            int idx = (instItemQueue[baseItemIndex]++ % instItems.Count);

            if (m_scrollItems.Count > i)
            {
                // 기존 아이템 재활용 (데이터 교체)
                m_scrollItems[i].Set(instItems[idx], baseItemIndex, i);
            }
            else
            {
                // 부족한 경우 새 연결 정보 추가
                AddScrollItem(new ScrollItem(instItems[idx], baseItemIndex, i));
            }
        }

        // ... (이후 잉여 데이터 정리 및 연결 리스트(NextItem) 세팅 로직 생략) ...
    }

    /// <summary>
    /// [핵심 로직 2. 가변 사이즈(Dynamic Size) 실시간 재사용 스크롤]
    /// 매 프레임 스크롤 위치를 감지하여 화면 밖으로 벗어난 UI 객체를 반대편으로 이동시킵니다.
    /// 모든 아이템의 크기가 동일하다는 가정을 버리고, GetItemSize()를 통해 
    /// 런타임에 동적으로 크기를 계산하여 정확한 위치(AnchorPos)에 객체를 재배치합니다.
    /// </summary>
    protected virtual void LateUpdate()
    {
        if (m_scrollItems.Count == 0) return;

        float contentScale = m_scrollRect.vertical ? m_rectXform.localScale.y : m_rectXform.localScale.x;

        // 최상단(현재 뷰포트에 걸친) 아이템의 동적 사이즈를 가져옴
        float itemSizeDelta = m_scrollItems[m_currentItemNo].GetItemSize(m_scrollRect.vertical);
        var anchoredPos = this.AnchoredPosition;

        // (수직 스크롤 기준 최적화 로직)
        if (m_scrollRect.vertical)
        {
            // 1. 위로 스크롤 시: 최상단 아이템이 화면 밖으로 완전히 벗어났을 때 (Top -> Bottom 이동)
            while ((anchoredPos - m_paddingHead - (m_diffPreFramePosition * contentScale)) > itemSizeDelta &&
                    m_currentItemNo + 1 < m_scrollItems.Count)
            {
                m_diffPreFramePosition += itemSizeDelta;

                ScrollItem currItem = m_scrollItems[m_currentItemNo];
                ScrollItem moveToNext = currItem.NextItem;

                if (moveToNext != null)
                {
                    // 최상단 객체를 최하단(NextItem) 위치로 이동시키고 데이터 갱신
                    currItem.Instance.SetAnchorPos(moveToNext.ItemAnchorPos);
                    OnUpdateItem(moveToNext);
                }

                ++m_currentItemNo;

                // 다음 기준이 될 아이템의 가변 사이즈로 갱신
                itemSizeDelta = m_scrollItems[m_currentItemNo].GetItemSize(m_scrollRect.vertical);
            }

            // 2. 아래로 스크롤 시: 화면 상단에 여백이 생겼을 때 (Bottom -> Top 이동)
            while ((anchoredPos - m_paddingHead - (m_diffPreFramePosition * contentScale)) < 0 &&
                    m_currentItemNo > 0)
            {
                ScrollItem prevItem = m_scrollItems[m_currentItemNo - 1];

                m_diffPreFramePosition -= prevItem.GetItemSize(m_scrollRect.vertical);

                ScrollItem moveToNext = prevItem.NextItem;

                if (moveToNext != null)
                {
                    // 최하단 객체를 최상단(PrevItem) 위치로 이동시키고 데이터 갱신
                    moveToNext.Instance.SetAnchorPos(prevItem.ItemAnchorPos);
                    OnUpdateItem(prevItem);
                }

                --m_currentItemNo;
            }
        }
        // ... (수평 스크롤 로직 생략) ...
    }
}