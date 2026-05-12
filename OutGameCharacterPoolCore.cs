using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

/// <summary>
/// [아웃게임 3D 캐릭터 및 파츠 비동기 풀링 매니저]
/// 로비, 락커룸 등에서 잦은 캐릭터 교체 및 파츠(장비) 변경 시 발생하는
/// 메모리 누수와 프레임 드랍을 방지하기 위해 설계된 비동기 리소스 매니저입니다.
/// </summary>
public class OutGameCharacterPool : MonoBehaviour
{
    private Queue<LoadingInfo> m_loadingQueue = new Queue<LoadingInfo>();
    protected LoadingInfo m_currentLoader;

    /// <summary>
    /// [핵심 로직 1. 비동기 로딩 큐(Queue) 상태 머신]
    /// 여러 캐릭터 및 파츠 로드 요청이 동시에 들어왔을 때 순서 대로 처리하기 위한 큐(Queue) 시스템입니다.
    /// Update문에서 Queue를 활용해 한 번에 하나의 로딩만 순차적으로 처리하며,
    /// 모든 하위 파츠(LoadPartsCounter)의 비동기 로딩이 완전히 끝난 시점을 캐치하여 콜백을 발생시킵니다.
    /// </summary>
    private void Update()
    {
        // 1. 현재 진행 중인 로딩이 완전히 완료되었는지 체크 (메쉬, 파츠 비동기 로딩 대기)
        if (m_currentLoader != null &&
            m_currentLoader.LoadPartsCounter == 0 &&
            m_currentLoader.LoadComplete == true)
        {
            // 로딩 완료 처리: 캐릭터를 지정된 부모 Transform에 부착
            var holderXform = m_currentLoader.m_holderTrans;
            if (holderXform)
            {
                var charXform = m_charResComp.transform;
                charXform.SetParent(holderXform);
                charXform.SetPositionAndRotation(holderXform.position, Quaternion.identity);
                charXform.localScale = Vector3.one;
            }

            m_playCompleteCb?.Invoke(m_charResComp);
            m_currentLoader = null; // 현재 로더 초기화
        }

        // 2. 현재 처리 중인 로딩이 없고, 대기열(Queue)에 요청이 있다면 다음 작업 시작
        if (m_loadingQueue.Count == 0 || m_currentLoader != null)
            return;

        m_currentLoader = m_loadingQueue.Dequeue();

        Despawn(m_currentLoader); // 기존 객체 풀 반환
        Spawn(m_currentLoader);   // 새로운 객체 생성 및 파츠 조립 시작
    }

    /// <summary>
    /// [핵심 로직 2. 스마트 풀링 및 부위별 파츠(Parts) 교체 최적화]
    /// 캐릭터를 완전히 파괴하고 새로 생성(Instantiate)하는 대신, 풀(Dictionary)에서 꺼내옵니다.
    /// 특히, 이미 생성된 캐릭터의 파츠(의상, 무기 등)를 교체할 때 모든 메쉬를 다시 로드하지 않고,
    /// 기존 장착 파츠와 비교하여 '변경된 파츠만' 선택적으로 비동기 로드하거나 Renderer를 켜고 끄는 최적화를 수행합니다.
    /// </summary>
    private void Spawn(LoadingInfo loadingInfo)
    {
        var dict = loadingInfo.m_charPoolDict;
        uint charTid = loadingInfo.m_charTid;

        // 1. 이미 메모리 풀에 해당 캐릭터가 존재하는 경우 (재사용)
        if (dict.TryGetValue(charTid, out var characterResObject))
        {
            var charRes = characterResObject.m_characterRes;
            charRes.transform.localPosition = Vector3.zero;
            m_currentLoader.LoadComplete = true; // 베이스 캐릭터 로드 패스

            // 파츠(Parts) 재활용 최적화 로직
            for (int i = 0; i < SkinTidSet.mc_numParts; i++)
            {
                var partType = (ePartType)i;
                var targetSkinTid = loadingInfo.m_skinTidSet.Get(partType);
                var charParts = charRes.Parts;

                // 이미 동일한 파츠를 장착하고 있다면 Renderer만 활성화 (Instantiate 생략)
                if (targetSkinTid == charParts.GetSkinTid(partType))
                {
                    var renderer = charParts.PartRenderer(partType);
                    if (renderer)
                    {
                        renderer.enabled = false;
                        renderer.gameObject.SetActive(true);
                    }
                }
                else
                {
                    // 파츠가 다를 경우에만 해당 부위 비동기 로딩 요청 (카운터 증가)
                    m_currentLoader.LoadPartsCounter += 1;
                    charParts.Instantiate(
                        partType,
                        targetSkinTid,
                        () => m_currentLoader.LoadPartsCounter -= 1); // 완료 시 카운터 차감
                }
            }

            m_playCompleteCb = loadingInfo.m_completeCb;
            m_charResComp = charRes;
        }
        // 2. 풀에 없는 새로운 캐릭터인 경우 Addressables를 통한 초기 비동기 로드 수행
        else
        {
            InitCharacter(charTid, loadingInfo.m_skinTidSet, (res) =>
            {
                dict.Add(charTid, new CharacterPoolObject(charTid, res));
                m_charResComp = res;
                m_playCompleteCb = loadingInfo.m_completeCb;
                m_currentLoader.LoadComplete = true;
            });
        }
    }
}