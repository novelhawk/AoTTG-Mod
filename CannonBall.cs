using Photon;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

public class CannonBall : Photon.MonoBehaviour
{
    private Vector3 correctPos;
    private Vector3 correctVelocity;
    public bool disabled;
    public Transform firingPoint;
    public bool isCollider;
    public HERO myHero;
    public List<TitanTrigger> myTitanTriggers;
    public float SmoothingDelay = 10f;

    private void Awake()
    {
        if (base.photonView != null)
        {
            base.photonView.observed = this;
            this.correctPos = base.transform.position;
            this.correctVelocity = Vector3.zero;
            base.GetComponent<SphereCollider>().enabled = false;
            if (base.photonView.isMine)
            {
                base.StartCoroutine(this.WaitAndDestroy(10f));
                this.myTitanTriggers = new List<TitanTrigger>();
            }
        }
    }

    public void destroyMe()
    {
        if (!this.disabled)
        {
            this.disabled = true;
            foreach (EnemyCheckCollider collider in PhotonNetwork.Instantiate("FX/boom4", base.transform.position, base.transform.rotation, 0).GetComponentsInChildren<EnemyCheckCollider>())
            {
                collider.dmg = 0;
            }
            if (RCSettings.deadlyCannons == 1)
            {
                foreach (HERO hero in FengGameManagerMKII.instance.GetPlayers())
                {
                    if (((hero != null) && (Vector3.Distance(hero.transform.position, base.transform.position) <= 20f)) && !hero.photonView.isMine)
                    {
                        GameObject gameObject = hero.gameObject;
                        PhotonPlayer owner = gameObject.GetPhotonView().owner;
                        if (((RCSettings.teamMode > 0) && (PhotonNetwork.player.customProperties[PhotonPlayerProperty.RCteam] != null)) && (owner.customProperties[PhotonPlayerProperty.RCteam] != null))
                        {
                            int num2 = RCextensions.returnIntFromObject(PhotonNetwork.player.customProperties[PhotonPlayerProperty.RCteam]);
                            int num3 = RCextensions.returnIntFromObject(owner.customProperties[PhotonPlayerProperty.RCteam]);
                            if ((num2 == 0) || (num2 != num3))
                            {
                                gameObject.GetComponent<HERO>().markDie();
                                gameObject.GetComponent<HERO>().photonView.RPC("netDie2", PhotonTargets.All, new object[] { -1, RCextensions.returnStringFromObject(PhotonNetwork.player.Name) + " " });
                                FengGameManagerMKII.instance.playerKillInfoUpdate(PhotonNetwork.player, 0);
                            }
                        }
                        else
                        {
                            gameObject.GetComponent<HERO>().markDie();
                            gameObject.GetComponent<HERO>().photonView.RPC("netDie2", PhotonTargets.All, new object[] { -1, RCextensions.returnStringFromObject(PhotonNetwork.player.Name) + " " });
                            FengGameManagerMKII.instance.playerKillInfoUpdate(PhotonNetwork.player, 0);
                        }
                    }
                }
            }
            if (this.myTitanTriggers != null)
            {
                for (int i = 0; i < this.myTitanTriggers.Count; i++)
                {
                    if (this.myTitanTriggers[i] != null)
                    {
                        this.myTitanTriggers[i].isCollide = false;
                    }
                }
            }
            PhotonNetwork.Destroy(base.gameObject);
        }
    }

    public void FixedUpdate()
    {
        if (base.photonView.isMine && !this.disabled)
        {
            LayerMask mask = ((int) 1) << LayerMask.NameToLayer("PlayerAttackBox");
            LayerMask mask2 = ((int) 1) << LayerMask.NameToLayer("EnemyBox");
            LayerMask mask3 = mask | mask2;
            if (!this.isCollider)
            {
                LayerMask mask4 = ((int) 1) << LayerMask.NameToLayer("Ground");
                mask3 |= mask4;
            }
            Collider[] colliderArray = Physics.OverlapSphere(base.transform.position, 0.6f, mask3.value);
            bool flag2 = false;
            for (int i = 0; i < colliderArray.Length; i++)
            {
                GameObject gameObject = colliderArray[i].gameObject;
                if (gameObject.layer == 16)
                {
                    TitanTrigger component = gameObject.GetComponent<TitanTrigger>();
                    if (!((component == null) || this.myTitanTriggers.Contains(component)))
                    {
                        component.isCollide = true;
                        this.myTitanTriggers.Add(component);
                    }
                }
                else if (gameObject.layer == 10)
                {
                    TITAN titan = gameObject.transform.root.gameObject.GetComponent<TITAN>();
                    if (titan != null)
                    {
                        if (titan.abnormalType == AbnormalType.TYPE_CRAWLER)
                        {
                            if (gameObject.name == "head")
                            {
                                titan.photonView.RPC("DieByCannon", titan.photonView.owner, new object[] { this.myHero.photonView.viewID });
                                titan.dieBlow(base.transform.position, 0.2f);
                                i = colliderArray.Length;
                            }
                        }
                        else if (gameObject.name == "head")
                        {
                            titan.photonView.RPC("DieByCannon", titan.photonView.owner, new object[] { this.myHero.photonView.viewID });
                            titan.dieHeadBlow(base.transform.position, 0.2f);
                            i = colliderArray.Length;
                        }
                        else if (UnityEngine.Random.Range((float) 0f, (float) 1f) < 0.5f)
                        {
                            titan.hitL(base.transform.position, 0.05f);
                        }
                        else
                        {
                            titan.hitR(base.transform.position, 0.05f);
                        }
                        this.destroyMe();
                    }
                }
                else if ((gameObject.layer == 9) && (gameObject.transform.root.name.Contains("CannonWall") || gameObject.transform.root.name.Contains("CannonGround")))
                {
                    flag2 = true;
                }
            }
            if (!(this.isCollider || flag2))
            {
                this.isCollider = true;
                base.GetComponent<SphereCollider>().enabled = true;
            }
        }
    }

    public void OnCollisionEnter(Collision myCollision)
    {
        if (base.photonView.isMine)
        {
            this.destroyMe();
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.isWriting)
        {
            stream.SendNext(base.transform.position);
            stream.SendNext(base.rigidbody.velocity);
        }
        else
        {
            this.correctPos = (Vector3) stream.ReceiveNext();
            this.correctVelocity = (Vector3) stream.ReceiveNext();
        }
    }

    public void Update()
    {
        if (!base.photonView.isMine)
        {
            base.transform.position = Vector3.Lerp(base.transform.position, this.correctPos, Time.deltaTime * this.SmoothingDelay);
            base.rigidbody.velocity = this.correctVelocity;
        }
    }

    public IEnumerator WaitAndDestroy(float time)
    {
        yield return new WaitForSeconds(time);
        this.destroyMe();
    }
}