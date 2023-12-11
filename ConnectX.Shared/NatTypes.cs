namespace ConnectX.Shared;

public enum NatTypes
{
    /// <summary>
    /// <para>Endpoint-Independent Mapping</para>
    /// <para>Endpoint-Independent Filtering</para>
    /// <para>Full-Cone NAT</para>
    /// </summary>
    Type1,

    /// <summary>
    /// <para>Endpoint-Independent Mapping</para>
    /// <para>Address-Independent Filtering</para>
    /// <para>Restricted-Cone NAT</para>
    /// </summary>
    Type2,

    /// <summary>
    /// <para>Endpoint-Independent Mapping</para>
    /// <para>Address and Port-Dependent Filtering</para>
    /// <para>Port-Restricted-Cone NAT</para>
    /// </summary>
    Type3,

    /// <summary>
    /// <para>Address-Dependent Mapping</para>
    /// <para>Endpoint-Independent Filtering</para>
    /// <para>Address-Restricted NAT</para>
    /// </summary>
    Type4,

    /// <summary>
    /// <para>Address-Dependent Mapping</para>
    /// <para>Address-Dependent Filtering</para>
    /// <para>Address and Port-Restricted NAT</para>
    /// </summary>
    Type5,

    /// <summary>
    /// <para>Address-Dependent Mapping</para>
    /// <para>Address and Port-Dependent Filtering</para>
    /// <para>Address and Port-Restricted NAT</para>
    /// </summary>
    Type6,

    /// <summary>
    /// <para>Address and Port-Dependent Mapping</para>
    /// <para>Endpoint-Independent Filtering</para>
    /// <para>Address and Port-Restricted NAT</para>
    /// </summary>
    Type7,

    /// <summary>
    /// <para>Address and Port-Dependent Mapping</para>
    /// <para>Address-Dependent Filtering</para>
    /// <para>Address and Port-Restricted NAT</para>
    /// </summary>
    Type8,

    /// <summary>
    /// <para>Address and Port-Dependent Mapping</para>
    /// <para>Address and Port-Dependent Filtering</para>
    /// <para>Symmetric NAT</para>
    /// </summary>
    Type9,
    
    Direct,
    Unknown
}